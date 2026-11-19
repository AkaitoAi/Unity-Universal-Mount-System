using System.Collections.Generic;
using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// Abstract base class for any mountable entity (car, motorcycle, horse, boat, aircraft, mech, turret, etc.).
    /// Provides multi-seat configuration, speed-gated mount/exit checks, body-surface proximity queries,
    /// Rigidbody motion telemetry, and automatic registration in <see cref="MountableRegistry"/>.
    /// </summary>
    public abstract class MountableControlAdapterBase : ControlTargetAdapterBase, IMountable, IMotionTelemetry
    {
        [Header("Mount & Seating Configuration")]
        [SerializeField] protected List<MountSeat> seats = new List<MountSeat>();
        [SerializeField] protected float maxBoardingSpeedKmh = 10f;
        [SerializeField] protected float maxExitSpeedKmh = 5f;
        [SerializeField] protected string mountActionLabel = "Enter";
        [SerializeField] protected string exitActionLabel = "Exit";
        [Tooltip("Optional icon representing this mount type (car, bike, horse, boat, aircraft, etc.) in the interaction UI.")]
        [SerializeField] private Sprite mountIcon;

        private Collider[] interactionColliders;

        public Sprite MountIcon => mountIcon;
        public override string MountActionLabel => string.IsNullOrEmpty(mountActionLabel) ? "Enter" : mountActionLabel;
        public override string ExitActionLabel => string.IsNullOrEmpty(exitActionLabel) ? "Exit" : exitActionLabel;

        public IReadOnlyList<MountSeat> Seats
        {
            get
            {
                if (seats.Count == 0) EnsureDefaultSeat();
                return seats;
            }
        }

        public MountSeat PrimarySeat
        {
            get
            {
                if (seats.Count == 0) EnsureDefaultSeat();
                return seats.Count > 0 ? seats[0] : null;
            }
        }

        public virtual Rigidbody Rigidbody => null;
        public virtual Vector3 Velocity => Rigidbody != null ? Rigidbody.velocity : Vector3.zero;
        public virtual float ForwardSpeed => Rigidbody != null ? Vector3.Dot(Rigidbody.velocity, transform.forward) : 0f;
        public virtual Transform MotionAnchor => transform;

        /// <summary>
        /// Current speed in km/h used for boarding and exit safety validation.
        /// Defaults to 3D Rigidbody velocity magnitude * 3.6f; subclasses can override with controller-specific speed.
        /// </summary>
        public virtual float CurrentSpeedKmh => Velocity.magnitude * 3.6f;

        protected virtual void OnValidate()
        {
            EnsureDefaultSeat();
        }

        protected override void Awake()
        {
            EnsureDefaultSeat();
            RefreshInteractionColliders();
            base.Awake();
        }

        /// <summary>
        /// Refreshes the cached collider array used for body-surface proximity detection.
        /// Call this if colliders are added or removed at runtime.
        /// </summary>
        public void RefreshInteractionColliders()
        {
            interactionColliders = GetComponentsInChildren<Collider>(true);
        }

        /// <summary>
        /// Finds the nearest solid body surface and its gap from the character capsule.
        /// The seat's entry anchor remains the boarding destination; proximity is measured around the whole body.
        /// </summary>
        public bool TryGetInteractionPoint(
            CapsuleCollider characterCapsule,
            Vector3 fallbackOrigin,
            out Vector3 interactionPoint,
            out float surfaceDistance)
        {
            interactionPoint = transform.position;
            surfaceDistance = float.MaxValue;
            if (interactionColliders == null) return false;

            bool hasCapsule = characterCapsule != null && characterCapsule.enabled && characterCapsule.gameObject.activeInHierarchy;
            Vector3 origin = hasCapsule ? characterCapsule.bounds.center : fallbackOrigin;
            bool found = false;

            for (int i = 0; i < interactionColliders.Length; i++)
            {
                Collider body = interactionColliders[i];
                if (body == null || !body.enabled || body.isTrigger || !body.gameObject.activeInHierarchy) continue;

                bool supported = body is BoxCollider || body is SphereCollider || body is CapsuleCollider ||
                    (body is MeshCollider mesh && mesh.convex && mesh.sharedMesh != null);
                if (!supported) continue;

                Vector3 bodyPoint = body.ClosestPoint(origin);
                Vector3 playerPoint = hasCapsule ? characterCapsule.ClosestPoint(bodyPoint) : origin;
                bodyPoint = body.ClosestPoint(playerPoint);
                if (hasCapsule) playerPoint = characterCapsule.ClosestPoint(bodyPoint);

                float distance = Vector3.Distance(playerPoint, bodyPoint);
                if (distance < surfaceDistance)
                {
                    surfaceDistance = distance;
                    interactionPoint = bodyPoint;
                    found = true;
                }
            }

            return found;
        }

        protected void EnsureDefaultSeat()
        {
            if (seats == null)
            {
                seats = new List<MountSeat>();
            }
            if (seats.Count == 0)
            {
                var defaultSeat = new MountSeat
                {
                    seatIndex = 0,
                    seatName = "PrimarySeat",
                    seatAnchor = transform,
                    entryApproachAnchor = transform,
                    exitCandidateAnchors = new Transform[0]
                };
                seats.Add(defaultSeat);
            }
        }

        public virtual void ConfigureMount(Transform seatAnchor, Transform approachAnchor, Transform[] exitAnchors)
        {
            if (seats == null || seats.Count == 0) EnsureDefaultSeat();
            MountSeat primary = seats[0];
            if (seatAnchor != null) primary.seatAnchor = seatAnchor;
            if (approachAnchor != null) primary.entryApproachAnchor = approachAnchor;
            if (exitAnchors != null && exitAnchors.Length > 0) primary.exitCandidateAnchors = exitAnchors;
        }

        #region IMountable Implementation

        /// <summary>
        /// Finds the closest unoccupied seat to <paramref name="characterPosition"/>, or null if all seats are occupied.
        /// </summary>
        public virtual MountSeat FindAvailableSeat(Vector3 characterPosition)
        {
            if (seats.Count == 0) EnsureDefaultSeat();

            MountSeat bestSeat = null;
            float bestSqrDist = float.MaxValue;

            for (int i = 0; i < seats.Count; i++)
            {
                MountSeat candidate = seats[i];
                if (candidate == null || candidate.IsOccupied) continue;

                Transform anchor = candidate.entryApproachAnchor != null
                    ? candidate.entryApproachAnchor
                    : (candidate.seatAnchor != null ? candidate.seatAnchor : transform);

                float sqrDist = (anchor.position - characterPosition).sqrMagnitude;
                if (sqrDist < bestSqrDist)
                {
                    bestSqrDist = sqrDist;
                    bestSeat = candidate;
                }
            }

            return bestSeat;
        }

        public virtual bool CanMount(GameObject character, out string reason)
        {
            if (!IsValid)
            {
                reason = "Target mount is invalid or disabled";
                return false;
            }

            float speed = CurrentSpeedKmh;
            if (speed > maxBoardingSpeedKmh)
            {
                reason = $"Mount moving too fast to enter ({speed:F1} km/h > {maxBoardingSpeedKmh} km/h)";
                return false;
            }

            Vector3 charPos = character != null ? character.transform.position : transform.position;
            if (FindAvailableSeat(charPos) == null)
            {
                reason = "All seats are occupied";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public virtual bool CanExit(GameObject character, out string reason)
        {
            float speed = CurrentSpeedKmh;
            var occupant = character != null ? character.GetComponent<CharacterControlAdapterBase>() : null;
            if (speed > maxExitSpeedKmh && (occupant == null || !occupant.CanExitMoving))
            {
                reason = $"Mount moving too fast to exit safely ({speed:F1} km/h > {maxExitSpeedKmh} km/h)";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public virtual bool ReserveSeat(MountSeat seat, GameObject character, long transactionId)
        {
            if (seat == null) return false;
            return seat.Reserve(character, transactionId);
        }

        public virtual void ReleaseSeatReservation(MountSeat seat, long transactionId)
        {
            if (seat != null)
            {
                seat.ReleaseReservation(transactionId);
            }
        }

        public virtual void OccupySeat(MountSeat seat, GameObject character)
        {
            if (seat != null)
            {
                seat.Occupy(character);
            }
        }

        public virtual void VacateSeat(MountSeat seat, GameObject character)
        {
            if (seat != null)
            {
                seat.Vacate();
            }
        }

        #endregion
    }
}
