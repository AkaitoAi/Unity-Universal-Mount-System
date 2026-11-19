using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace AkaitoAi.UniversalMountSystem.Adapters
{
    /// <summary>
    /// Ready-to-use, zero-dependency third-person character adapter.
    /// Works with ANY third-person controller (Unity Starter Assets, Invector, Opsive,
    /// Kinematic Character Controller, or custom CharacterController/Rigidbody scripts)
    /// and both Legacy Input Manager and New Input System projects.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-30)]
    public class GenericCharacterControlAdapter : CharacterControlAdapterBase, ICharacterApproachDriver
    {
        [Header("Character Physics & Components (Auto-Detected If Empty)")]
        [SerializeField] private CharacterController characterController;
        [SerializeField] private Rigidbody characterRigidbody;
        [SerializeField] private CapsuleCollider characterCapsule;
        [SerializeField] private Animator characterAnimator;

        [Header("Seat Attachment & Visuals")]
        [Tooltip("FollowAnchor (Recommended) syncs pose in LateUpdate without reparenting — prevents non-uniform mount scale distortion and ensures the player survives if the mount is destroyed mid-ride.")]
        [SerializeField] private SeatAttachmentMode attachmentMode = SeatAttachmentMode.FollowAnchor;
        [Tooltip("Optional list of renderers to hide when a seat has hideOccupantVisuals = true. Auto-collected from children if empty.")]
        [SerializeField] private Renderer[] visualRenderers;
        [Tooltip("Optional Animator bool parameter set to true while mounted and false on dismount.")]
        [SerializeField] private string mountedBoolParameter = "";
        [Tooltip("Optional Animator int parameter set to the occupied seatIndex while mounted.")]
        [SerializeField] private string seatIndexIntParameter = "";

        [Header("Automated Approach (Walk-To-Mount)")]
        [Tooltip("Enable automated walking/running around the mount to reach the seat's entry anchor.")]
        [SerializeField] private bool enableAutoApproach = true;
        [Tooltip("Movement speed (m/s) used during automated approach.")]
        [Min(0.5f)] [SerializeField] private float approachMoveSpeed = 4.5f;
        [Tooltip("Rotation speed (deg/s) toward the approach direction.")]
        [Min(30f)] [SerializeField] private float approachTurnSpeed = 540f;
        [Tooltip("Optional Animator float parameter driven with normalized approach speed (0..1) during automated approach.")]
        [SerializeField] private string approachSpeedFloatParameter = "";
#pragma warning disable 0414
        [Tooltip("Legacy Input Manager axes checked to cancel automated approach when the player steers manually. For New Input System, set ManualMovementInputProvider in code.")]
        [SerializeField] private string horizontalAxisName = "Horizontal";
        [SerializeField] private string verticalAxisName = "Vertical";
#pragma warning restore 0414

        [Header("Moving Exit Support")]
        [Tooltip("Allow dismounting while the mount is moving faster than maxExitSpeedKmh.")]
        [SerializeField] private bool supportMovingExit = false;

        [Header("Events (Optional Inspector Hooks)")]
        [SerializeField] private UnityEvent<MountSeat> onMountedEvent;
        [SerializeField] private UnityEvent onDismountedEvent;
        [SerializeField] private UnityEvent<MovingExitParams> onMovingExitEvent;

        public override event Action OnCharacterDead;
        public override event Action OnCharacterRevived;

        /// <summary>
        /// Optional delegate for reading manual movement input (e.g. from Unity's New Input System Move action)
        /// to cancel automated approach when the player steers manually.
        /// </summary>
        public Func<Vector2> ManualMovementInputProvider { get; set; }

        private bool isAttached;
        private bool isDead;
        private bool isInputActive = true;
        private bool isApproaching;
        private Transform followedSeatAnchor;

        // Pending Rigidbody approach velocity applied in FixedUpdate
        private bool hasPendingRbApproachVelocity;
        private Vector3 pendingRbApproachDir;

        // Captured pre-mount state for rollback/restoration
        private Transform savedParent;
        private Vector3 savedPosition;
        private Quaternion savedRotation;
        private Vector3 savedLocalScale = Vector3.one;
        private bool savedCharacterControllerEnabled;
        private bool savedRigidbodyKinematic;
        private bool savedRigidbodyDetectCollisions;
        private readonly List<Collider> disabledColliders = new List<Collider>();
        private readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        private readonly List<bool> savedBehaviourStates = new List<bool>();

        public override bool IsAttached => isAttached;
        public override bool IsDead => isDead;
        public override bool CanExitMoving => supportMovingExit && !isDead;
        public override bool CanBoard => !isDead && !isAttached;
        public override CapsuleCollider CapsuleCollider => characterCapsule;
        public override Rigidbody Rigidbody => characterRigidbody;

        public override float FootPivotOffset
        {
            get
            {
                float scaleY = Mathf.Abs(transform.lossyScale.y);
                if (characterController != null)
                {
                    return Mathf.Max(0f, (characterController.height * 0.5f - characterController.center.y) * scaleY);
                }
                if (characterCapsule != null)
                {
                    return Mathf.Max(0f, (characterCapsule.height * 0.5f - characterCapsule.center.y) * scaleY);
                }
                return 0f;
            }
        }

        public override bool IsGrounded
        {
            get
            {
                if (characterController != null && characterController.enabled)
                {
                    return characterController.isGrounded;
                }
                Vector3 footOrigin = transform.position - Vector3.up * FootPivotOffset + Vector3.up * 0.25f;
                return Physics.Raycast(footOrigin, Vector3.down, 0.55f, ~0, QueryTriggerInteraction.Ignore);
            }
        }

        public override Vector3 Velocity
        {
            get
            {
                if (characterController != null && characterController.enabled) return characterController.velocity;
                if (characterRigidbody != null) return UnityCompat.GetLinearVelocity(characterRigidbody);
                return Vector3.zero;
            }
        }

        public override float ForwardSpeed => Vector3.Dot(Velocity, transform.forward);

        protected virtual void OnValidate()
        {
            AutoDetectComponents();
        }

        protected override void Awake()
        {
            AutoDetectComponents();
            savedLocalScale = transform.localScale;
            base.Awake();
        }

        private void LateUpdate()
        {
            if (isAttached && attachmentMode == SeatAttachmentMode.FollowAnchor && followedSeatAnchor != null)
            {
                transform.SetPositionAndRotation(followedSeatAnchor.position, followedSeatAnchor.rotation);
            }
        }

        private void FixedUpdate()
        {
            if (isApproaching && hasPendingRbApproachVelocity && characterRigidbody != null && !characterRigidbody.isKinematic)
            {
                Vector3 vel = UnityCompat.GetLinearVelocity(characterRigidbody);
                UnityCompat.SetLinearVelocity(characterRigidbody, new Vector3(
                    pendingRbApproachDir.x * approachMoveSpeed,
                    vel.y,
                    pendingRbApproachDir.z * approachMoveSpeed));
            }
        }

        private void AutoDetectComponents()
        {
            if (characterController == null) characterController = GetComponent<CharacterController>();
            if (characterRigidbody == null) characterRigidbody = GetComponent<Rigidbody>();
            if (characterCapsule == null) characterCapsule = GetComponent<CapsuleCollider>();
            if (characterAnimator == null) characterAnimator = GetComponentInChildren<Animator>();
        }

        public void NotifyCharacterDead()
        {
            if (isDead) return;
            isDead = true;
            OnCharacterDead?.Invoke();
        }

        public void NotifyCharacterRevived()
        {
            if (!isDead) return;
            isDead = false;
            OnCharacterRevived?.Invoke();
        }

        public override void NeutralizeInputs()
        {
            isInputActive = false;
            ApplyGenericControlState(false);

            if (characterRigidbody != null && !characterRigidbody.isKinematic)
            {
                UnityCompat.SetLinearVelocity(characterRigidbody, Vector3.zero);
                characterRigidbody.angularVelocity = Vector3.zero;
            }
        }

        public override void SetInputActive(bool active)
        {
            isInputActive = active && HasLease && !isAttached && !isDead && !isApproaching;
            ApplyGenericControlState(isInputActive);
        }

        public override void CaptureState()
        {
            savedParent = transform.parent;
            savedPosition = transform.position;
            savedRotation = transform.rotation;
            savedLocalScale = transform.localScale;
            savedCharacterControllerEnabled = characterController != null && characterController.enabled;

            if (characterRigidbody != null)
            {
                savedRigidbodyKinematic = characterRigidbody.isKinematic;
                savedRigidbodyDetectCollisions = characterRigidbody.detectCollisions;
            }
        }

        public override void Attach(Transform mountAnchor, MountSeat seat)
        {
            Transform targetAnchor = (seat != null && seat.seatAnchor != null) ? seat.seatAnchor : mountAnchor;

            // Support in-place seat switching while already attached
            if (isAttached)
            {
                MoveToSeatAnchor(targetAnchor, seat);
                onMountedEvent?.Invoke(seat);
                return;
            }

            EndApproachDrive();
            NeutralizeInputs();

            if (characterController != null)
            {
                savedCharacterControllerEnabled = characterController.enabled;
                characterController.enabled = false;
            }

            if (characterRigidbody != null)
            {
                savedRigidbodyKinematic = characterRigidbody.isKinematic;
                savedRigidbodyDetectCollisions = characterRigidbody.detectCollisions;
                if (!characterRigidbody.isKinematic)
                {
                    UnityCompat.SetLinearVelocity(characterRigidbody, Vector3.zero);
                    characterRigidbody.angularVelocity = Vector3.zero;
                }
                characterRigidbody.isKinematic = true;
                characterRigidbody.detectCollisions = false;
            }

            disabledColliders.Clear();
            Collider[] cols = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null && cols[i].enabled && !(cols[i] is CharacterController))
                {
                    cols[i].enabled = false;
                    disabledColliders.Add(cols[i]);
                }
            }

            MoveToSeatAnchor(targetAnchor, seat);

            isAttached = true;
            onMountedEvent?.Invoke(seat);
        }

        private void MoveToSeatAnchor(Transform targetAnchor, MountSeat seat)
        {
            // Restore previously hidden renderers first (in case switching between hidden/visible seats)
            for (int i = 0; i < hiddenRenderers.Count; i++)
            {
                if (hiddenRenderers[i] != null) hiddenRenderers[i].enabled = true;
            }
            hiddenRenderers.Clear();

            bool hideVisuals = seat != null && seat.hideOccupantVisuals;
            if (hideVisuals)
            {
                Renderer[] renderers = (visualRenderers != null && visualRenderers.Length > 0)
                    ? visualRenderers
                    : GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] != null && renderers[i].enabled)
                    {
                        renderers[i].enabled = false;
                        hiddenRenderers.Add(renderers[i]);
                    }
                }
            }

            if (targetAnchor != null)
            {
                if (attachmentMode == SeatAttachmentMode.ParentToAnchor)
                {
                    followedSeatAnchor = null;
                    transform.SetParent(targetAnchor, false);
                    transform.localPosition = Vector3.zero;
                    transform.localRotation = Quaternion.identity;
                }
                else
                {
                    followedSeatAnchor = targetAnchor;
                    transform.SetPositionAndRotation(targetAnchor.position, targetAnchor.rotation);
                }
            }

            UpdateAnimatorMountState(true, seat != null ? seat.seatIndex : 0);
        }

        public override void Detach(Vector3 exitWorldPosition, Quaternion exitWorldRotation)
        {
            if (!isAttached) return;

            followedSeatAnchor = null;

            if (attachmentMode == SeatAttachmentMode.ParentToAnchor)
            {
                transform.SetParent(savedParent, true);
                transform.localScale = savedLocalScale;
            }

            transform.SetPositionAndRotation(exitWorldPosition, exitWorldRotation);

            for (int i = 0; i < hiddenRenderers.Count; i++)
            {
                if (hiddenRenderers[i] != null) hiddenRenderers[i].enabled = true;
            }
            hiddenRenderers.Clear();

            for (int i = 0; i < disabledColliders.Count; i++)
            {
                if (disabledColliders[i] != null) disabledColliders[i].enabled = true;
            }
            disabledColliders.Clear();

            if (characterRigidbody != null)
            {
                characterRigidbody.isKinematic = savedRigidbodyKinematic;
                characterRigidbody.detectCollisions = savedRigidbodyDetectCollisions;
                if (!characterRigidbody.isKinematic)
                {
                    UnityCompat.SetLinearVelocity(characterRigidbody, Vector3.zero);
                    characterRigidbody.angularVelocity = Vector3.zero;
                }
            }

            if (characterController != null)
            {
                characterController.enabled = savedCharacterControllerEnabled;
            }

            UpdateAnimatorMountState(false, 0);

            isAttached = false;
            onDismountedEvent?.Invoke();
        }

        public override void RestoreState()
        {
            if (isAttached)
            {
                Detach(savedPosition, savedRotation);
            }
            else
            {
                followedSeatAnchor = null;
                transform.SetParent(savedParent, true);
                transform.localScale = savedLocalScale;
                transform.SetPositionAndRotation(savedPosition, savedRotation);
            }
        }

        public override void ApplyMovingExit(MovingExitParams exitParams)
        {
            if (characterRigidbody != null && !characterRigidbody.isKinematic)
            {
                Vector3 impulseVelocity = exitParams.baseVelocity
                    + exitParams.outwardDirection * exitParams.outwardImpulse
                    + Vector3.up * exitParams.upwardImpulse;
                UnityCompat.SetLinearVelocity(characterRigidbody, impulseVelocity);
            }

            onMovingExitEvent?.Invoke(exitParams);
        }

        private void UpdateAnimatorMountState(bool mounted, int seatIndex)
        {
            if (characterAnimator == null || !characterAnimator.isActiveAndEnabled) return;

            if (!string.IsNullOrEmpty(mountedBoolParameter))
            {
                characterAnimator.SetBool(mountedBoolParameter, mounted);
            }
            if (!string.IsNullOrEmpty(seatIndexIntParameter))
            {
                characterAnimator.SetInteger(seatIndexIntParameter, seatIndex);
            }
        }

        #region ICharacterApproachDriver Implementation

        public bool CanStartApproach => enableAutoApproach && CanBoard && HasLease && !isApproaching;
        public bool IsApproachStateValid => isApproaching && CanBoard && HasLease && isActiveAndEnabled;

        public bool BeginApproachDrive()
        {
            if (!CanStartApproach) return false;

            isApproaching = true;
            hasPendingRbApproachVelocity = false;
            savedBehaviourStates.Clear();
            if (controlBehaviours != null)
            {
                for (int i = 0; i < controlBehaviours.Length; i++)
                {
                    Behaviour b = controlBehaviours[i];
                    savedBehaviourStates.Add(b != null && b.enabled);
                    if (b != null && b != this)
                    {
                        b.enabled = false;
                    }
                }
            }

            return true;
        }

        public void DriveApproachStep(Vector3 nextWaypointWorld, float deltaTime)
        {
            if (!isApproaching || deltaTime <= 0f) return;

            Vector3 toTarget = nextWaypointWorld - transform.position;
            toTarget.y = 0f;
            float dist = toTarget.magnitude;
            if (dist < 0.02f)
            {
                hasPendingRbApproachVelocity = false;
                return;
            }

            Vector3 dir = toTarget / dist;
            Quaternion targetRot = Quaternion.LookRotation(dir, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, approachTurnSpeed * deltaTime);

            if (characterController != null && characterController.enabled)
            {
                hasPendingRbApproachVelocity = false;
                float step = Mathf.Min(dist, approachMoveSpeed * deltaTime);
                Vector3 move = dir * step;
                move.y = Physics.gravity.y * deltaTime;
                characterController.Move(move);
            }
            else if (characterRigidbody != null && !characterRigidbody.isKinematic)
            {
                pendingRbApproachDir = dir;
                hasPendingRbApproachVelocity = true;
            }
            else
            {
                hasPendingRbApproachVelocity = false;
                float step = Mathf.Min(dist, approachMoveSpeed * deltaTime);
                transform.position += dir * step;
            }

            if (characterAnimator != null && !string.IsNullOrEmpty(approachSpeedFloatParameter))
            {
                characterAnimator.SetFloat(approachSpeedFloatParameter, 1f);
            }
        }

        public void EndApproachDrive()
        {
            if (!isApproaching) return;

            isApproaching = false;
            hasPendingRbApproachVelocity = false;

            if (characterAnimator != null && !string.IsNullOrEmpty(approachSpeedFloatParameter))
            {
                characterAnimator.SetFloat(approachSpeedFloatParameter, 0f);
            }

            if (characterRigidbody != null && !characterRigidbody.isKinematic)
            {
                Vector3 vel = UnityCompat.GetLinearVelocity(characterRigidbody);
                UnityCompat.SetLinearVelocity(characterRigidbody, new Vector3(0f, vel.y, 0f));
            }

            if (controlBehaviours != null)
            {
                for (int i = 0; i < controlBehaviours.Length; i++)
                {
                    Behaviour b = controlBehaviours[i];
                    if (b != null && b != this && i < savedBehaviourStates.Count)
                    {
                        b.enabled = savedBehaviourStates[i] && HasLease && !isAttached && !isDead;
                    }
                }
            }
            savedBehaviourStates.Clear();
        }

        public Vector2 ReadManualMovementInput()
        {
            if (ManualMovementInputProvider != null)
            {
                return ManualMovementInputProvider();
            }

#if ENABLE_LEGACY_INPUT_MANAGER
            float h = !string.IsNullOrEmpty(horizontalAxisName) ? Input.GetAxisRaw(horizontalAxisName) : 0f;
            float v = !string.IsNullOrEmpty(verticalAxisName) ? Input.GetAxisRaw(verticalAxisName) : 0f;
            return new Vector2(h, v);
#else
            return Vector2.zero;
#endif
        }

        #endregion
    }
}
