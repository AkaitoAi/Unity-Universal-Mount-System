using System;
using System.Collections.Generic;
using AkaitoAi.UniversalMountSystem.Adapters;
using AkaitoAi.UniversalMountSystem.Configuration;
using UnityEngine;
using UnityEngine.Events;

namespace AkaitoAi.UniversalMountSystem.Services
{
    /// <summary>
    /// Master manager for mount and control sessions in a scene.
    /// Coordinates <see cref="ControlSessionCoordinator"/>, <see cref="CameraAuthority"/>,
    /// <see cref="InputAuthorityRouter"/>, <see cref="WorldRelevanceResolver"/>, safe exit clearance checks,
    /// moving exits, and automatic recovery if an active mount is destroyed.
    /// Works with any third-person controller adapter and any number/type of mounts.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-500)]
    public class MountSessionManager : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("Optional ScriptableObject settings asset. If unassigned, sensible runtime defaults are created automatically.")]
        [SerializeField] private MountSessionSettingsSO settings;

        [Header("Scene Actors & Subsystems")]
        [SerializeField] private CharacterControlAdapterBase characterAdapter;
        [SerializeField] private ControlTargetAdapterBase currentMount;
        [SerializeField] private CameraAuthority cameraAuthority;
        [SerializeField] private InputAuthorityRouter inputAuthorityRouter;
        [SerializeField] private WorldRelevanceResolver worldRelevanceResolver;

        [Header("Unity Events (Optional Inspector Hooks)")]
        [SerializeField] private UnityEvent<GameObject> onMountEntered;
        [SerializeField] private UnityEvent<GameObject> onMountExited;
        [SerializeField] private UnityEvent<string> onTransitionFailedEvent;

        private readonly ControlSessionCoordinator coordinator = new ControlSessionCoordinator();
        private readonly Collider[] overlapBuffer = new Collider[64];
        private readonly RaycastHit[] raycastBuffer = new RaycastHit[32];

        private MountSessionSettingsSO runtimeFallbackSettings;
        private Vector3 lastRecordedSafePosition;
        private Quaternion lastRecordedSafeRotation;
        private float lastSafePoseRecordTime;
        private bool characterDeathHandled;

        public event Action<ControlContext, ControlContext, string> OnContextCommitted;
        public event Action<long, string, ControlMode> OnTransitionFailed;
        public event Action<IControlTarget, MountSeat> OnMountEntered;
        public event Action<IControlTarget> OnMountExited;

        public ControlSessionCoordinator Coordinator => coordinator;
        public ControlContext CurrentContext => coordinator.CurrentContext;
        public ControlMode CurrentMode => coordinator.CurrentContext.Mode;
        public bool IsTransitioning => coordinator.IsTransitioning;
        public CharacterControlAdapterBase CharacterAdapter => characterAdapter;
        public ControlTargetAdapterBase CurrentMount => currentMount;
        public IControlTarget CurrentTarget => coordinator.CurrentContext.ControlledTarget;
        public IControlTarget CurrentMountTarget => CurrentMode == ControlMode.Mounted ? CurrentTarget : currentMount;
        public CameraAuthority CameraAuthority => cameraAuthority;
        public InputAuthorityRouter InputRouter => inputAuthorityRouter;
        public WorldRelevanceResolver RelevanceResolver => worldRelevanceResolver;

        public T GetCharacterAdapter<T>() where T : CharacterControlAdapterBase => characterAdapter as T;
        public T GetCurrentMount<T>() where T : ControlTargetAdapterBase => currentMount as T;

        private static MountSessionManager activeInstance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic()
        {
            activeInstance = null;
        }

        public static MountSessionManager Instance => activeInstance;
        public static bool HasInstance => activeInstance != null;

        public static bool TryGet(out MountSessionManager manager)
        {
            if (activeInstance == null)
            {
                activeInstance = FindObjectOfType<MountSessionManager>();
            }
            manager = activeInstance;
            return manager != null;
        }

        public MountSessionSettingsSO Settings
        {
            get
            {
                if (settings != null) return settings;
                if (runtimeFallbackSettings == null)
                {
                    runtimeFallbackSettings = ScriptableObject.CreateInstance<MountSessionSettingsSO>();
                    runtimeFallbackSettings.name = "RuntimeDefault_MountSessionSettings";
                }
                return runtimeFallbackSettings;
            }
            set => settings = value;
        }

        protected virtual void Awake()
        {
            if (activeInstance != null && activeInstance != this && activeInstance.gameObject != null)
            {
                Debug.LogWarning("[MountSessionManager] Duplicate MountSessionManager detected. Destroying duplicate.", this);
                Destroy(gameObject);
                return;
            }
            activeInstance = this;

            ResolveSubsystems();
            HookCoordinatorEvents();
            coordinator.IsInputAllowed = () => inputAuthorityRouter == null || !inputAuthorityRouter.IsSuspended;
            ReconcileMountables();
        }

        private void OnEnable()
        {
            HookCoordinatorEvents();
            coordinator.IsInputAllowed = () => inputAuthorityRouter == null || !inputAuthorityRouter.IsSuspended;

            if (characterAdapter != null)
            {
                characterAdapter.OnCharacterDead -= HandleCharacterDeath;
                characterAdapter.OnCharacterDead += HandleCharacterDeath;
            }

            if (inputAuthorityRouter != null)
            {
                inputAuthorityRouter.OnSuspensionChanged -= HandleSuspensionChanged;
                inputAuthorityRouter.OnSuspensionChanged += HandleSuspensionChanged;
            }
        }

        private void OnDisable()
        {
            if (characterAdapter != null)
            {
                characterAdapter.OnCharacterDead -= HandleCharacterDeath;
            }

            if (inputAuthorityRouter != null)
            {
                inputAuthorityRouter.OnSuspensionChanged -= HandleSuspensionChanged;
            }

            UnhookCoordinatorEvents();
        }

        protected virtual void OnDestroy()
        {
            if (characterAdapter != null && characterAdapter.IsAttached)
            {
                characterAdapter.RestoreState();
            }

            coordinator.Shutdown();

            if (runtimeFallbackSettings != null)
            {
                Destroy(runtimeFallbackSettings);
                runtimeFallbackSettings = null;
            }

            if (activeInstance == this)
            {
                activeInstance = null;
            }
        }

        private void Start()
        {
            InitializeSessionOnFoot();
        }

        private void Update()
        {
            CheckTargetValidityAndRecover();
            RecordSafePosePeriodic();
        }

        /// <summary>
        /// Assigns or swaps the active on-foot character adapter at runtime (e.g. after spawning the player).
        /// </summary>
        public void SetCharacterAdapter(CharacterControlAdapterBase newCharacterAdapter, bool reinitializeSession = true)
        {
            if (characterAdapter != null)
            {
                characterAdapter.OnCharacterDead -= HandleCharacterDeath;
            }

            characterAdapter = newCharacterAdapter;
            characterDeathHandled = false;

            if (characterAdapter != null && isActiveAndEnabled)
            {
                characterAdapter.OnCharacterDead -= HandleCharacterDeath;
                characterAdapter.OnCharacterDead += HandleCharacterDeath;
            }

            if (reinitializeSession && characterAdapter != null)
            {
                InitializeSessionOnFoot();
            }
        }

        /// <summary>
        /// Registers all active mountable adapters in loaded scenes and ensures unleased mounts have neutralized inputs.
        /// Call this after dynamically spawning new mounts if needed.
        /// </summary>
        public void ReconcileMountables()
        {
            ControlTargetAdapterBase[] allAdapters = Resources.FindObjectsOfTypeAll<ControlTargetAdapterBase>();
            for (int i = 0; i < allAdapters.Length; i++)
            {
                ControlTargetAdapterBase adapter = allAdapters[i];
                if (adapter == null || !adapter.gameObject.scene.isLoaded) continue;
                if (adapter == characterAdapter || adapter is CharacterControlAdapterBase) continue;

                IMountable mountable = adapter.GetCapability<IMountable>();
                if (mountable != null)
                {
                    if (adapter.gameObject.activeInHierarchy && adapter.IsValid)
                    {
                        MountableRegistry.Register(adapter);
                    }

                    if (!adapter.HasLease)
                    {
                        adapter.NeutralizeInputs();
                    }
                }
            }
        }

        private void ResolveSubsystems()
        {
            if (characterAdapter == null) characterAdapter = FindObjectOfType<CharacterControlAdapterBase>(true);
            if (cameraAuthority == null) cameraAuthority = GetComponent<CameraAuthority>() ?? gameObject.AddComponent<CameraAuthority>();
            if (inputAuthorityRouter == null) inputAuthorityRouter = GetComponent<InputAuthorityRouter>() ?? gameObject.AddComponent<InputAuthorityRouter>();
            if (worldRelevanceResolver == null) worldRelevanceResolver = GetComponent<WorldRelevanceResolver>() ?? gameObject.AddComponent<WorldRelevanceResolver>();
        }

        private void HookCoordinatorEvents()
        {
            coordinator.OnContextCommitted -= HandleContextCommitted;
            coordinator.OnContextCommitted += HandleContextCommitted;
            coordinator.OnTransitionFailed -= HandleTransitionFailed;
            coordinator.OnTransitionFailed += HandleTransitionFailed;
        }

        private void UnhookCoordinatorEvents()
        {
            coordinator.OnContextCommitted -= HandleContextCommitted;
            coordinator.OnTransitionFailed -= HandleTransitionFailed;
        }

        private void InitializeSessionOnFoot()
        {
            if (characterAdapter == null)
            {
                characterAdapter = FindObjectOfType<CharacterControlAdapterBase>();
            }

            if (characterAdapter != null)
            {
                lastRecordedSafePosition = characterAdapter.transform.position;
                lastRecordedSafeRotation = characterAdapter.transform.rotation;

                Camera initialCam = cameraAuthority != null
                    ? cameraAuthority.GetCameraForTarget(characterAdapter, ControlMode.OnFoot)
                    : Camera.main;

                coordinator.Initialize("LocalPlayer", characterAdapter.gameObject, characterAdapter, initialCam, characterAdapter.transform);

                if (cameraAuthority != null)
                {
                    cameraAuthority.SwitchToTarget(characterAdapter, ControlMode.OnFoot);
                }

                if (worldRelevanceResolver != null)
                {
                    worldRelevanceResolver.UpdateRelevance(coordinator.CurrentContext);
                }
            }
        }

        public ControlTransitionResult RequestEnter(IControlTarget mountTarget, MountSeat targetSeat = null)
        {
            if (characterAdapter != null && !characterAdapter.CanBoard)
            {
                return ControlTransitionResult.Failed(0, "Character cannot board right now", ControlMode.Mounted);
            }

            if (!coordinator.CurrentContext.IsValid)
            {
                return ControlTransitionResult.Failed(0, "Session is not initialized", ControlMode.Mounted);
            }

            if (coordinator.IsTransitioning)
            {
                return ControlTransitionResult.Failed(0, "Coordinator is busy transitioning", ControlMode.Mounted);
            }

            if (characterAdapter == null || !characterAdapter.IsValid || characterAdapter.IsDead)
            {
                return ControlTransitionResult.Failed(0, "Character is dead, disabled, or invalid", ControlMode.Mounted);
            }

            if (inputAuthorityRouter != null && inputAuthorityRouter.IsSuspended)
            {
                return ControlTransitionResult.Failed(0, "Input is suspended", ControlMode.Mounted);
            }

            if (mountTarget == null || !mountTarget.IsValid)
            {
                return ControlTransitionResult.Failed(0, "Target mount is disabled, destroyed, or invalid", ControlMode.Mounted);
            }

            MountSessionSettingsSO s = Settings;
            IMountable mountable = mountTarget.GetCapability<IMountable>();
            if (mountable == null)
            {
                return ControlTransitionResult.Failed(0, "Target does not support IMountable", ControlMode.Mounted);
            }

            Vector3 characterPos = characterAdapter.transform.position;
            MountSeat seat = targetSeat ?? mountable.FindAvailableSeat(characterPos) ?? mountable.PrimarySeat;
            if (seat == null)
            {
                return ControlTransitionResult.Failed(0, "No available seat on mount", ControlMode.Mounted);
            }

            Transform approachAnchor = seat.entryApproachAnchor != null
                ? seat.entryApproachAnchor
                : (seat.seatAnchor != null ? seat.seatAnchor : mountTarget.Transform);

            float distToApproach = Vector3.Distance(characterPos, approachAnchor.position);
            if (distToApproach > s.interactionRange)
            {
                return ControlTransitionResult.Failed(0, $"Approach distance {distToApproach:F1}m exceeds interaction range {s.interactionRange}m", ControlMode.Mounted);
            }

            // Line-of-sight check
            Vector3 rayStart = characterPos + Vector3.up * 1.0f;
            Vector3 rayEnd = approachAnchor.position + Vector3.up * 1.0f;
            Vector3 rayDir = rayEnd - rayStart;
            float rayDist = rayDir.magnitude;
            if (rayDist > 0.25f)
            {
                int losHitCount = Physics.RaycastNonAlloc(rayStart, rayDir.normalized, raycastBuffer, rayDist, s.obstacleLayerMask, QueryTriggerInteraction.Ignore);
                if (losHitCount >= raycastBuffer.Length)
                {
                    return ControlTransitionResult.Failed(0, "Line of sight query overflow", ControlMode.Mounted);
                }

                for (int i = 0; i < losHitCount; i++)
                {
                    Collider hitCol = raycastBuffer[i].collider;
                    if (hitCol != null && !hitCol.isTrigger &&
                        !hitCol.transform.IsChildOf(characterAdapter.transform) &&
                        !hitCol.transform.IsChildOf(mountTarget.Transform))
                    {
                        return ControlTransitionResult.Failed(0, "Line of sight to mount entry is blocked by an obstacle", ControlMode.Mounted);
                    }
                }
            }

            // Speed check
            IMotionTelemetry telemetry = mountTarget.GetCapability<IMotionTelemetry>();
            float speedKmh = telemetry != null ? telemetry.Velocity.magnitude * 3.6f : 0f;
            if (speedKmh > s.maxBoardingSpeedKmh)
            {
                return ControlTransitionResult.Failed(0, $"Mount moving too fast ({speedKmh:F1} km/h > {s.maxBoardingSpeedKmh} km/h)", ControlMode.Mounted);
            }

            Camera targetCam = cameraAuthority != null
                ? cameraAuthority.GetCameraForTarget(mountTarget, ControlMode.Mounted)
                : Camera.main;

            characterAdapter.CaptureState();

            if (inputAuthorityRouter != null)
            {
                inputAuthorityRouter.SuspendInput("TRANSITION");
                inputAuthorityRouter.FlushInteractionEdges();
            }

            try
            {
                return coordinator.RequestTransitionToMounted(
                    mountTarget,
                    seat,
                    targetCam,
                    prepareCallback: () =>
                    {
                        characterAdapter.Attach(mountTarget.Transform, seat);
                        return true;
                    },
                    commitCallback: () =>
                    {
                        if (inputAuthorityRouter != null)
                        {
                            inputAuthorityRouter.ResumeInput("TRANSITION");
                        }
                        currentMount = ResolveControlAdapter(mountTarget);
                        if (cameraAuthority != null)
                        {
                            cameraAuthority.SwitchToTarget(mountTarget, ControlMode.Mounted);
                        }
                    },
                    rollbackCallback: () =>
                    {
                        characterAdapter.RestoreState();
                        if (cameraAuthority != null)
                        {
                            cameraAuthority.SwitchToTarget(characterAdapter, ControlMode.OnFoot);
                        }
                    }
                );
            }
            finally
            {
                if (inputAuthorityRouter != null)
                {
                    inputAuthorityRouter.ResumeInput("TRANSITION");
                }
            }
        }

        public ControlTransitionResult RequestExit()
        {
            if (CurrentMode != ControlMode.Mounted)
            {
                return ControlTransitionResult.Failed(0, "Cannot exit when not mounted", ControlMode.OnFoot);
            }

            if (coordinator.IsTransitioning)
            {
                return ControlTransitionResult.Failed(0, "Coordinator is busy transitioning", ControlMode.OnFoot);
            }

            if (characterAdapter == null || !characterAdapter.IsValid || characterAdapter.IsDead)
            {
                return ControlTransitionResult.Failed(0, "Character is dead, disabled, or invalid", ControlMode.OnFoot);
            }

            if (inputAuthorityRouter != null && inputAuthorityRouter.IsSuspended)
            {
                return ControlTransitionResult.Failed(0, "Input is suspended", ControlMode.OnFoot);
            }

            IControlTarget activeMount = CurrentContext.ControlledTarget;
            MountSeat currentSeat = CurrentContext.OccupiedSeat;

            if (!FindSafeExitPlacement(activeMount, currentSeat, out Vector3 safePos, out Quaternion safeRot, out string rejectReason))
            {
                return ControlTransitionResult.Failed(0, rejectReason, ControlMode.OnFoot);
            }

            MountSessionSettingsSO s = Settings;
            IMotionTelemetry exitTelemetry = activeMount.GetCapability<IMotionTelemetry>();
            Vector3 exitVelocity = exitTelemetry != null ? exitTelemetry.Velocity : Vector3.zero;
            bool movingExit = exitVelocity.magnitude * 3.6f > s.maxExitSpeedKmh;
            var mountAdapter = activeMount as MountableControlAdapterBase;
            Vector3 mountAngularVelocity = mountAdapter != null && mountAdapter.Rigidbody != null
                ? mountAdapter.Rigidbody.angularVelocity
                : Vector3.zero;

            if (movingExit && mountAdapter != null && mountAdapter.Rigidbody != null)
            {
                exitVelocity = mountAdapter.Rigidbody.GetPointVelocity(characterAdapter.transform.position);
            }

            Camera charCam = cameraAuthority != null
                ? cameraAuthority.GetCameraForTarget(characterAdapter, ControlMode.OnFoot)
                : Camera.main;

            if (inputAuthorityRouter != null)
            {
                inputAuthorityRouter.SuspendInput("TRANSITION");
                inputAuthorityRouter.FlushInteractionEdges();
            }

            try
            {
                ControlTransitionResult result = coordinator.RequestTransitionToOnFoot(
                    characterAdapter,
                    safePos,
                    safeRot,
                    charCam,
                    prepareCallback: () =>
                    {
                        characterAdapter.Detach(safePos, safeRot);
                        return true;
                    },
                    commitCallback: () =>
                    {
                        if (inputAuthorityRouter != null)
                        {
                            inputAuthorityRouter.ResumeInput("TRANSITION");
                        }
                        currentMount = null;
                        if (cameraAuthority != null)
                        {
                            cameraAuthority.SwitchToTarget(characterAdapter, ControlMode.OnFoot);
                        }
                    },
                    rollbackCallback: () =>
                    {
                        if (activeMount != null)
                        {
                            characterAdapter.Attach(activeMount.Transform, currentSeat);
                        }
                    }
                );

                if (result.Success && movingExit)
                {
                    Vector3 outwardDir = Vector3.ProjectOnPlane(safePos - activeMount.Transform.position, Vector3.up);
                    if (outwardDir.sqrMagnitude < 0.01f)
                    {
                        outwardDir = Vector3.ProjectOnPlane(-activeMount.Transform.right, Vector3.up);
                    }
                    outwardDir = outwardDir.sqrMagnitude > 0.001f ? outwardDir.normalized : Vector3.right;

                    var exitParams = new MovingExitParams
                    {
                        baseVelocity = exitVelocity,
                        outwardDirection = outwardDir,
                        exitedMountRoot = activeMount.Transform,
                        rollMaxSpeedKmh = s.movingExitRollMaxSpeedKmh,
                        outwardImpulse = s.movingExitOutwardImpulse,
                        upwardImpulse = s.movingExitUpwardImpulse,
                        diveDuration = s.movingExitDiveDuration,
                        tumbleTorque = s.movingExitTumbleTorque,
                        recoveryDelay = s.movingExitRecoveryDelay
                    };

                    characterAdapter.ApplyMovingExit(exitParams);
                    if (activeMount is GenericMountControlAdapter genericAdapter)
                    {
                        genericAdapter.ResumeMotionAfterMovingExit(
                            exitTelemetry != null ? exitTelemetry.Velocity : exitVelocity,
                            mountAngularVelocity);
                    }
                }

                return result;
            }
            finally
            {
                if (inputAuthorityRouter != null)
                {
                    inputAuthorityRouter.ResumeInput("TRANSITION");
                }
            }
        }

        public bool FindSafeExitPlacement(IControlTarget mountTarget, MountSeat seat, out Vector3 safePosition, out Quaternion safeRotation, out string reason)
        {
            safePosition = Vector3.zero;
            safeRotation = Quaternion.identity;

            if (mountTarget == null || !mountTarget.IsValid)
            {
                reason = "Invalid mount target";
                return false;
            }

            MountSessionSettingsSO s = Settings;
            Transform mountT = mountTarget.Transform;

            if (Vector3.Dot(mountT.up, Vector3.up) < 0.2f)
            {
                reason = "Exit blocked: mount is severely rolled or flipped";
                return false;
            }

            IMotionTelemetry telemetry = mountTarget.GetCapability<IMotionTelemetry>();
            float speedKmh = telemetry != null ? telemetry.Velocity.magnitude * 3.6f : 0f;

            if (speedKmh > s.maxExitSpeedKmh &&
                (!s.allowMovingExit || characterAdapter == null || !characterAdapter.CanExitMoving))
            {
                reason = $"Mount moving too fast ({speedKmh:F1} km/h > {s.maxExitSpeedKmh:F1} km/h)";
                return false;
            }

            bool isMovingExit = speedKmh > s.maxExitSpeedKmh;

            if (seat != null && seat.exitCandidateAnchors != null && seat.exitCandidateAnchors.Length > 0)
            {
                for (int i = 0; i < seat.exitCandidateAnchors.Length; i++)
                {
                    Transform anchor = seat.exitCandidateAnchors[i];
                    if (anchor != null && EvaluateExitSpot(mountT, anchor.position, anchor.rotation, out safePosition, out safeRotation, isMovingExit))
                    {
                        reason = string.Empty;
                        return true;
                    }
                }
            }

            // Fallback generated positions (left, right, rear)
            Vector3 leftSpot = mountT.position - mountT.right * 1.8f + Vector3.up * 0.2f;
            Vector3 rightSpot = mountT.position + mountT.right * 1.8f + Vector3.up * 0.2f;
            Vector3 rearSpot = mountT.position - mountT.forward * 2.8f + Vector3.up * 0.2f;

            if (EvaluateExitSpot(mountT, leftSpot, mountT.rotation, out safePosition, out safeRotation, isMovingExit) ||
                EvaluateExitSpot(mountT, rightSpot, mountT.rotation, out safePosition, out safeRotation, isMovingExit) ||
                EvaluateExitSpot(mountT, rearSpot, mountT.rotation, out safePosition, out safeRotation, isMovingExit))
            {
                reason = string.Empty;
                return true;
            }

            reason = "Exit blocked: no safe position clear of obstacles";
            return false;
        }

        private bool EvaluateExitSpot(Transform mountTransform, Vector3 probePos, Quaternion probeRot, out Vector3 resolvedPos, out Quaternion resolvedRot, bool movingExit)
        {
            resolvedPos = probePos;
            resolvedRot = probeRot;

            MountSessionSettingsSO s = Settings;

            // 1. Ground raycast probe
            Vector3 rayOrigin = new Vector3(probePos.x, Mathf.Max(probePos.y, mountTransform.position.y) + 2.5f, probePos.z);
            int groundHits = Physics.RaycastNonAlloc(rayOrigin, Vector3.down, raycastBuffer, s.groundCheckDistance + 10.0f, s.groundLayerMask, QueryTriggerInteraction.Ignore);
            if (groundHits == 0 || groundHits >= raycastBuffer.Length)
            {
                return false;
            }

            float closestDist = float.MaxValue;
            RaycastHit bestHit = default;
            bool hitFound = false;

            for (int i = 0; i < groundHits; i++)
            {
                RaycastHit h = raycastBuffer[i];
                if (h.collider != null &&
                    !h.collider.transform.IsChildOf(mountTransform) &&
                    (characterAdapter == null || !h.collider.transform.IsChildOf(characterAdapter.transform)))
                {
                    if (h.distance < closestDist)
                    {
                        closestDist = h.distance;
                        bestHit = h;
                        hitFound = true;
                    }
                }
            }

            if (!hitFound) return false;

            float slopeAngle = Vector3.Angle(bestHit.normal, Vector3.up);
            if (slopeAngle > s.maxGroundSlopeAngle) return false;

            Vector3 groundSurfacePos = bestHit.point;

            // 2. Character capsule overlap test
            float radius = s.characterCapsuleRadius;
            float height = s.characterCapsuleHeight;
            if (characterAdapter != null && characterAdapter.CapsuleCollider != null)
            {
                Vector3 scale = characterAdapter.transform.lossyScale;
                radius = characterAdapter.CapsuleCollider.radius * Mathf.Max(scale.x, scale.z);
                height = characterAdapter.CapsuleCollider.height * scale.y;
            }

            float skinOffset = 0.05f;
            Vector3 point1 = groundSurfacePos + Vector3.up * (radius + skinOffset);
            Vector3 point2 = groundSurfacePos + Vector3.up * Mathf.Max(radius + skinOffset + 0.05f, height - radius - skinOffset);

            int overlapCount = Physics.OverlapCapsuleNonAlloc(point1, point2, radius, overlapBuffer, s.obstacleLayerMask, QueryTriggerInteraction.Ignore);
            if (overlapCount >= overlapBuffer.Length) return false;

            Rigidbody mountRb = mountTransform.GetComponent<Rigidbody>();

            for (int i = 0; i < overlapCount; i++)
            {
                Collider col = overlapBuffer[i];
                if (col == null || col.isTrigger) continue;
                if (characterAdapter != null && col.transform.IsChildOf(characterAdapter.transform)) continue;

                bool belongsToMount = col.transform.IsChildOf(mountTransform) || (mountRb != null && col.attachedRigidbody == mountRb);
                if (belongsToMount)
                {
                    if (movingExit) return false;
                    continue;
                }

                if (col == bestHit.collider)
                {
                    if (col is MeshCollider mc && !mc.convex)
                    {
                        Ray downRay = new Ray(groundSurfacePos + Vector3.up * (height + 0.2f), Vector3.down);
                        if (mc.Raycast(downRay, out RaycastHit downHit, height + 0.5f) && downHit.point.y > groundSurfacePos.y + 0.4f)
                        {
                            return false;
                        }
                    }
                    else
                    {
                        Vector3 closestPt = col.ClosestPoint(point2);
                        if (Vector3.Distance(closestPt, point2) < radius - 0.01f)
                        {
                            return false;
                        }
                    }
                    continue;
                }

                return false;
            }

            // 3. Capsule sweep along exit path
            Vector3 sweepStart = mountTransform.position + Vector3.up * 0.8f;
            Vector3 sweepEnd = groundSurfacePos + Vector3.up * (height * 0.5f);
            Vector3 sweepDir = sweepEnd - sweepStart;
            float sweepDist = sweepDir.magnitude;

            if (sweepDist > 0.1f)
            {
                float sweepRadius = radius * 0.8f;
                Vector3 sweepP1 = sweepStart - Vector3.up * (height * 0.25f);
                Vector3 sweepP2 = sweepStart + Vector3.up * (height * 0.25f);

                int sweepHits = Physics.CapsuleCastNonAlloc(sweepP1, sweepP2, sweepRadius, sweepDir.normalized, raycastBuffer, sweepDist, s.obstacleLayerMask, QueryTriggerInteraction.Ignore);
                if (sweepHits >= raycastBuffer.Length) return false;

                for (int i = 0; i < sweepHits; i++)
                {
                    Collider pathCol = raycastBuffer[i].collider;
                    if (pathCol == null || pathCol.isTrigger) continue;
                    if (pathCol.transform.IsChildOf(mountTransform)) continue;
                    if (mountRb != null && pathCol.attachedRigidbody == mountRb) continue;
                    if (characterAdapter != null && pathCol.transform.IsChildOf(characterAdapter.transform)) continue;

                    if (pathCol == bestHit.collider && raycastBuffer[i].point.y <= groundSurfacePos.y + 0.4f)
                    {
                        continue;
                    }

                    return false;
                }
            }

            resolvedPos = groundSurfacePos;
            resolvedRot = Quaternion.Euler(0f, probeRot.eulerAngles.y, 0f);
            return true;
        }

        private void CheckTargetValidityAndRecover()
        {
            if (CurrentMode != ControlMode.Mounted || coordinator.IsTransitioning) return;

            IControlTarget target = CurrentContext.ControlledTarget;
            bool isTargetInvalid = target == null || (target is UnityEngine.Object uo && uo == null) || !target.IsValid;
            if (!isTargetInvalid) return;

            if (Settings.enableDebugLogs)
            {
                Debug.LogWarning("[MountSessionManager] Active mounted target became invalid or was destroyed. Recovering to on-foot.");
            }

            if (characterAdapter != null && characterAdapter.IsAttached)
            {
                characterAdapter.Detach(lastRecordedSafePosition, lastRecordedSafeRotation);
            }

            if (characterAdapter == null || characterAdapter.IsDead)
            {
                HandleCharacterDeath();
                return;
            }

            Camera charCam = cameraAuthority != null
                ? cameraAuthority.GetCameraForTarget(characterAdapter, ControlMode.OnFoot)
                : Camera.main;

            coordinator.RecoverToOnFoot(lastRecordedSafePosition, lastRecordedSafeRotation, characterAdapter, charCam);
            if (cameraAuthority != null)
            {
                cameraAuthority.SwitchToTarget(characterAdapter, ControlMode.OnFoot);
            }
        }

        private void RecordSafePosePeriodic()
        {
            if (CurrentMode != ControlMode.OnFoot || characterAdapter == null || !characterAdapter.IsValid || characterAdapter.IsDead)
            {
                return;
            }

            if (Time.time < lastSafePoseRecordTime + 1.0f || !characterAdapter.IsGrounded) return;

            LayerMask groundMask = Settings.groundLayerMask;
            if (Physics.Raycast(characterAdapter.transform.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 1.5f, groundMask, QueryTriggerInteraction.Ignore))
            {
                lastSafePoseRecordTime = Time.time;
                lastRecordedSafePosition = hit.point;
                lastRecordedSafeRotation = characterAdapter.transform.rotation;
            }
        }

        private void HandleCharacterDeath()
        {
            if (characterDeathHandled) return;
            characterDeathHandled = true;

            if (inputAuthorityRouter != null)
            {
                inputAuthorityRouter.SuspendInput("DEATH");
            }

            if (coordinator.IsTransitioning)
            {
                coordinator.CancelActiveTransition("Character death");
            }

            if (characterAdapter != null && characterAdapter.IsAttached)
            {
                characterAdapter.Detach(characterAdapter.transform.position, characterAdapter.transform.rotation);
            }
        }

        private void HandleSuspensionChanged(bool suspended)
        {
            if (suspended)
            {
                if (CurrentMode == ControlMode.OnFoot && characterAdapter != null && characterAdapter.IsValid)
                {
                    characterAdapter.SetInputActive(false);
                }
                else if (CurrentMode == ControlMode.Mounted && CurrentContext.ControlledTarget != null && CurrentContext.ControlledTarget.IsValid)
                {
                    CurrentContext.ControlledTarget.SetInputActive(false);
                }
            }
            else
            {
                if (CurrentMode == ControlMode.OnFoot && characterAdapter != null && characterAdapter.IsValid && !characterAdapter.IsDead)
                {
                    characterAdapter.SetInputActive(true);
                }
                else if (CurrentMode == ControlMode.Mounted && CurrentContext.ControlledTarget != null && CurrentContext.ControlledTarget.IsValid)
                {
                    CurrentContext.ControlledTarget.SetInputActive(true);
                }
            }
        }

        private void HandleContextCommitted(ControlContext previousContext, ControlContext newContext, string reason)
        {
            if (newContext.Mode == ControlMode.Mounted && newContext.ControlledTarget != null)
            {
                currentMount = ResolveControlAdapter(newContext.ControlledTarget);
                OnMountEntered?.Invoke(newContext.ControlledTarget, newContext.OccupiedSeat);
                onMountEntered?.Invoke(newContext.ControlledTarget.TargetGameObject);
            }
            else
            {
                IControlTarget exitedMount = previousContext.Mode == ControlMode.Mounted ? previousContext.ControlledTarget : null;
                currentMount = null;
                if (exitedMount != null)
                {
                    OnMountExited?.Invoke(exitedMount);
                    onMountExited?.Invoke(exitedMount.TargetGameObject);
                }
            }

            if (worldRelevanceResolver != null)
            {
                worldRelevanceResolver.UpdateRelevance(newContext);
            }

            OnContextCommitted?.Invoke(previousContext, newContext, reason);
        }

        private void HandleTransitionFailed(long txId, string failureReason, ControlMode requestedMode)
        {
            if (Settings.enableDebugLogs)
            {
                Debug.LogWarning($"[MountSessionManager] Transition failed ({requestedMode}, tx={txId}): {failureReason}");
            }

            OnTransitionFailed?.Invoke(txId, failureReason, requestedMode);
            onTransitionFailedEvent?.Invoke(failureReason);
        }

        private static ControlTargetAdapterBase ResolveControlAdapter(IControlTarget target)
        {
            if (target == null) return null;
            if (target is ControlTargetAdapterBase directAdapter) return directAdapter;
            GameObject go = target.TargetGameObject;
            if (go == null) return null;
            return go.GetComponent<ControlTargetAdapterBase>()
                ?? go.GetComponentInChildren<ControlTargetAdapterBase>(true)
                ?? go.GetComponentInParent<ControlTargetAdapterBase>(true);
        }
    }
}
