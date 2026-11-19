using AkaitoAi.UniversalMountSystem.Services;
using UnityEngine;
using UnityEngine.Events;

namespace AkaitoAi.UniversalMountSystem.Adapters
{
    /// <summary>
    /// Ready-to-use, zero-dependency mount adapter for ANY type of mount (car, motorcycle, horse,
    /// boat, aircraft, mech, turret, hoverboard, etc.).
    /// Assign your mount's controller script(s) in <c>controlBehaviours</c>, optional AudioSources,
    /// optional per-mount <c>customCameraRig</c>, and 1..N <c>seats</c> in the Inspector.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-25)]
    public class GenericMountControlAdapter : MountableControlAdapterBase
    {
        [Header("Generic Mount Physics")]
        [SerializeField] private Rigidbody mountRigidbody;
        [Tooltip("If true, sets the Rigidbody to kinematic when parked/uncontrolled.")]
        [SerializeField] private bool freezePhysicsWhenUncontrolled = false;
        [Tooltip("If true, zeroes linear and angular velocity when losing control (unless coasting from a moving exit).")]
        [SerializeField] private bool zeroVelocityOnRelease = true;

        [Header("Events (Optional Inspector Hooks)")]
        [SerializeField] private UnityEvent<GameObject> onRiderMounted;
        [SerializeField] private UnityEvent<GameObject> onRiderDismounted;
        [SerializeField] private UnityEvent onControlActivated;
        [SerializeField] private UnityEvent onControlDeactivated;

        private bool isInputActive;
        private bool coastingUnoccupied;
        private float coastElapsed;

        public bool IsInputActive => isInputActive;
        public override Rigidbody Rigidbody => mountRigidbody;

        public void ResumeMotionAfterMovingExit(Vector3 velocity, Vector3 angularVelocity)
        {
            if (HasLease || mountRigidbody == null) return;
            coastingUnoccupied = true;
            coastElapsed = 0f;
            mountRigidbody.isKinematic = false;
            mountRigidbody.velocity = velocity;
            mountRigidbody.angularVelocity = angularVelocity;
        }

        protected virtual void FixedUpdate()
        {
            if (HasLease || !coastingUnoccupied || mountRigidbody == null) return;

            float decel = 5.0f;
            float maxDuration = 4.0f;
            if (MountSessionManager.TryGet(out var manager) && manager.Settings != null)
            {
                decel = manager.Settings.mountCoastDeceleration;
                maxDuration = manager.Settings.mountCoastMaxDuration;
            }

            coastElapsed += Time.fixedDeltaTime;

            Vector3 currentVel = mountRigidbody.velocity;
            Vector3 planarVel = new Vector3(currentVel.x, 0f, currentVel.z);
            Vector3 nextPlanar = Vector3.MoveTowards(planarVel, Vector3.zero, decel * Time.fixedDeltaTime);
            mountRigidbody.velocity = new Vector3(nextPlanar.x, currentVel.y, nextPlanar.z);
            mountRigidbody.angularVelocity = Vector3.MoveTowards(
                mountRigidbody.angularVelocity, Vector3.zero, decel * 0.5f * Time.fixedDeltaTime);

            if (nextPlanar.sqrMagnitude < 0.12f || coastElapsed >= maxDuration)
            {
                coastingUnoccupied = false;
                coastElapsed = 0f;
                if (zeroVelocityOnRelease && !mountRigidbody.isKinematic)
                {
                    mountRigidbody.velocity = Vector3.zero;
                    mountRigidbody.angularVelocity = Vector3.zero;
                }
                if (freezePhysicsWhenUncontrolled)
                {
                    mountRigidbody.isKinematic = true;
                }
            }
        }

        protected override void OnValidate()
        {
            if (mountRigidbody == null) mountRigidbody = GetComponent<Rigidbody>();
            base.OnValidate();
        }

        protected override void Awake()
        {
            if (mountRigidbody == null) mountRigidbody = GetComponent<Rigidbody>();
            base.Awake();

            if (!HasLease)
            {
                NeutralizeInputs();
            }
        }

        protected virtual void Start()
        {
            if (!HasLease)
            {
                NeutralizeInputs();
            }
        }

        protected override void OnDisable()
        {
            coastingUnoccupied = false;
            coastElapsed = 0f;
            base.OnDisable();
        }

        public override void OccupySeat(MountSeat seat, GameObject character)
        {
            base.OccupySeat(seat, character);
            onRiderMounted?.Invoke(character);
        }

        public override void VacateSeat(MountSeat seat, GameObject character)
        {
            base.VacateSeat(seat, character);
            onRiderDismounted?.Invoke(character);
        }

        public override void NeutralizeInputs()
        {
            bool wasActive = isInputActive;
            isInputActive = false;
            ApplyGenericControlState(false);

            if (mountRigidbody != null && !coastingUnoccupied)
            {
                if (zeroVelocityOnRelease && !mountRigidbody.isKinematic)
                {
                    mountRigidbody.velocity = Vector3.zero;
                    mountRigidbody.angularVelocity = Vector3.zero;
                }
                if (freezePhysicsWhenUncontrolled)
                {
                    mountRigidbody.isKinematic = true;
                }
            }

            if (wasActive)
            {
                onControlDeactivated?.Invoke();
            }
        }

        public override void SetInputActive(bool active)
        {
            bool nextActive = active && HasLease;
            bool changed = nextActive != isInputActive;
            isInputActive = nextActive;

            if (isInputActive)
            {
                coastingUnoccupied = false;
                coastElapsed = 0f;
                if (!gameObject.activeSelf)
                {
                    gameObject.SetActive(true);
                }
                if (mountRigidbody != null)
                {
                    if (freezePhysicsWhenUncontrolled)
                    {
                        mountRigidbody.isKinematic = false;
                    }
                    mountRigidbody.WakeUp();
                }
            }

            ApplyGenericControlState(isInputActive);

            if (changed)
            {
                if (isInputActive) onControlActivated?.Invoke();
                else onControlDeactivated?.Invoke();
            }
        }

        protected override void OnLeaseReleased(long transactionId)
        {
            NeutralizeInputs();
        }
    }
}
