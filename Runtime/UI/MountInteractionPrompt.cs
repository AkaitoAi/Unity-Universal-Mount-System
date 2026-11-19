using System;
using System.Collections.Generic;
using System.Reflection;
using AkaitoAi.UniversalMountSystem.Services;
using UnityEngine;
using UnityEngine.UI;

namespace AkaitoAi.UniversalMountSystem.UI
{
    /// <summary>
    /// Controller-agnostic interaction prompt, proximity detector, automated approach runner,
    /// and optional mobile/contextual UI switcher.
    ///
    /// Discovers any number of registered mounts via <see cref="MountableRegistry"/> (zero FindObjectsOfType),
    /// supports both standard UnityEngine.UI.Text and TextMeshPro (without requiring a compile-time TMP reference),
    /// and works with any third-person controller adapter.
    /// </summary>
    [DisallowMultipleComponent]
    public class MountInteractionPrompt : MonoBehaviour
    {
        [Header("UI References (All Optional)")]
        [SerializeField] private GameObject promptRoot;
        [Tooltip("Assign any UI Text or TextMeshProUGUI component to display the full prompt (e.g. 'Press [E] to Enter').")]
        [SerializeField] private Component promptTextComponent;
        [SerializeField] private Button interactionButton;
        [Tooltip("Assign any UI Text or TextMeshProUGUI component on the interaction button to display the short action label (e.g. 'Enter', 'Ride', 'Exit').")]
        [SerializeField] private Component buttonLabelComponent;
        [SerializeField] private Image mountIconImage;

        [Header("Contextual / Mobile UI Groups (Optional)")]
        [Tooltip("Default UI root for on-foot controls (hidden via CanvasGroup so touch axes stay alive).")]
        [SerializeField] private GameObject defaultOnFootUiRoot;
        [Tooltip("Default UI root for mounted controls (used when a mount has no dedicated mobileControlsRoot).")]
        [SerializeField] private GameObject defaultMountedUiRoot;

        [Header("Prompt Formatting")]
        [SerializeField] private string promptFormat = "Press [{0}] to {1}";
        [SerializeField] private string keyDisplayLabel = "E";
        [SerializeField] private string cancelActionLabel = "Cancel";

        [Header("Detection Settings")]
        [Tooltip("If true, uses obstacleMaskOverride instead of MountSessionSettingsSO.obstacleLayerMask.")]
        [SerializeField] private bool overrideObstacleMask = false;
        [SerializeField] private LayerMask obstacleMaskOverride = ~0;
        [SerializeField] private float detectionInterval = 0.12f;

        public Button InteractionButton => interactionButton;
        public IControlTarget NearbyTarget => nearbyTarget;
        public bool IsApproaching => approachTarget != null;

        private float nextDetectionTime;
        private IControlTarget nearbyTarget;
        private readonly RaycastHit[] losHits = new RaycastHit[32];

        private bool hasInitializedVisuals;
        private bool lastVisible;
        private string lastActionText = string.Empty;

        private bool isSubscribed;
        private GameObject activeCustomMobileRoot;

        private IControlTarget approachTarget;
        private MountSeat approachSeat;
        private ICharacterApproachDriver approachDriver;
        private Vector2 initialManualInput;
        private bool manualInputReleased;
        private Vector3 progressPosition;
        private float progressTime;
        private float approachStartedTime;
        private float approachMaximumDistance;
        private readonly MountApproachPathResolver approachPathResolver = new MountApproachPathResolver();

        private static readonly Dictionary<Type, PropertyInfo> CachedTextProperties = new Dictionary<Type, PropertyInfo>();

        private void Start()
        {
            EnsureSubscribed();
        }

        private void OnEnable()
        {
            if (interactionButton != null)
            {
                interactionButton.onClick.AddListener(OnInteractionButtonClicked);
            }

            EnsureSubscribed();

            hasInitializedVisuals = false;
            UpdateVisuals(false, string.Empty);

            if (MountSessionManager.TryGet(out var manager))
            {
                ReconcileContextualUi(manager.CurrentMode, manager.CurrentContext.ControlledTarget);
            }
            else
            {
                ReconcileContextualUi(ControlMode.OnFoot, null);
            }
        }

        private void OnDisable()
        {
            CancelApproach();

            if (interactionButton != null)
            {
                interactionButton.onClick.RemoveListener(OnInteractionButtonClicked);
            }

            if (MountSessionManager.TryGet(out var manager))
            {
                if (manager.InputRouter != null)
                {
                    manager.InputRouter.OnInteractionTriggered -= HandleInteractionTriggered;
                }
                if (manager.Coordinator != null)
                {
                    manager.Coordinator.OnContextCommitted -= HandleContextCommitted;
                }
            }

            isSubscribed = false;
            UpdateVisuals(false, string.Empty);
        }

        private void EnsureSubscribed()
        {
            if (isSubscribed) return;

            if (!MountSessionManager.TryGet(out var manager))
            {
                manager = GetComponent<MountSessionManager>();
            }

            if (manager != null && manager.InputRouter != null)
            {
                manager.InputRouter.OnInteractionTriggered -= HandleInteractionTriggered;
                manager.InputRouter.OnInteractionTriggered += HandleInteractionTriggered;
                if (manager.Coordinator != null)
                {
                    manager.Coordinator.OnContextCommitted -= HandleContextCommitted;
                    manager.Coordinator.OnContextCommitted += HandleContextCommitted;
                }
                isSubscribed = true;
            }
        }

        private void HandleContextCommitted(ControlContext previous, ControlContext current, string reason)
        {
            CancelApproach();
            ReconcileContextualUi(current.Mode, current.ControlledTarget);
        }

        private void ReconcileContextualUi(ControlMode mode, IControlTarget controlledTarget = null)
        {
            GameObject targetUiRoot = null;
            if (controlledTarget != null)
            {
                IMobileControlUIProvider uiProvider = controlledTarget.GetCapability<IMobileControlUIProvider>() ?? (controlledTarget as IMobileControlUIProvider);
                if (uiProvider != null)
                {
                    targetUiRoot = uiProvider.MobileControlsRoot;
                }
            }

            if (activeCustomMobileRoot != null && activeCustomMobileRoot != targetUiRoot)
            {
                if (activeCustomMobileRoot != defaultOnFootUiRoot && activeCustomMobileRoot != defaultMountedUiRoot)
                {
                    activeCustomMobileRoot.SetActive(false);
                }
                activeCustomMobileRoot = null;
            }

            if (targetUiRoot != null)
            {
                activeCustomMobileRoot = targetUiRoot;
                if (defaultOnFootUiRoot != null && defaultOnFootUiRoot != targetUiRoot)
                {
                    SetCanvasGroupVisibility(defaultOnFootUiRoot, false);
                }
                if (defaultMountedUiRoot != null && defaultMountedUiRoot != targetUiRoot)
                {
                    defaultMountedUiRoot.SetActive(false);
                }
                targetUiRoot.SetActive(true);
                return;
            }

            if (mode == ControlMode.Mounted)
            {
                if (defaultOnFootUiRoot != null) SetCanvasGroupVisibility(defaultOnFootUiRoot, false);
                if (defaultMountedUiRoot != null) defaultMountedUiRoot.SetActive(true);
            }
            else
            {
                if (defaultOnFootUiRoot != null) SetCanvasGroupVisibility(defaultOnFootUiRoot, true);
                if (defaultMountedUiRoot != null) defaultMountedUiRoot.SetActive(false);
            }
        }

        private static void SetCanvasGroupVisibility(GameObject root, bool visible)
        {
            if (root == null) return;
            CanvasGroup cg = root.GetComponent<CanvasGroup>();
            if (cg == null) cg = root.AddComponent<CanvasGroup>();
            cg.alpha = visible ? 1f : 0f;
            cg.interactable = visible;
            cg.blocksRaycasts = visible;
            if (visible && !root.activeSelf) root.SetActive(true);
        }

        private void Update()
        {
            if (!isSubscribed) EnsureSubscribed();

            if (IsApproaching)
            {
                TickApproach(Time.deltaTime);
            }

            if (Time.time < nextDetectionTime) return;
            nextDetectionTime = Time.time + detectionInterval;

            EvaluateProximityAndState();
        }

        private void EvaluateProximityAndState()
        {
            if (!MountSessionManager.TryGet(out var manager) || manager.IsTransitioning)
            {
                UpdateVisuals(false, string.Empty);
                return;
            }

            if (manager.InputRouter != null && manager.InputRouter.IsSuspended)
            {
                UpdateVisuals(false, string.Empty);
                return;
            }

            if (IsApproaching)
            {
                UpdateVisuals(true, cancelActionLabel, approachTarget);
                return;
            }

            if (manager.CurrentMode == ControlMode.Mounted)
            {
                nearbyTarget = null;
                string exitAction = "Exit";
                IControlTarget activeTarget = manager.CurrentContext.ControlledTarget;
                if (activeTarget != null)
                {
                    IMobileControlUIProvider uiProvider = activeTarget.GetCapability<IMobileControlUIProvider>() ?? (activeTarget as IMobileControlUIProvider);
                    if (uiProvider != null && !string.IsNullOrEmpty(uiProvider.ExitActionLabel))
                    {
                        exitAction = uiProvider.ExitActionLabel;
                    }
                }
                UpdateVisuals(true, exitAction, activeTarget);
                return;
            }

            CharacterControlAdapterBase character = manager.CharacterAdapter;
            Transform playerT = character != null ? character.transform : null;
            if (playerT == null)
            {
                UpdateVisuals(false, string.Empty);
                return;
            }

            ICharacterApproachDriver driver = character.GetCapability<ICharacterApproachDriver>();
            bool supportsApproach = driver != null && driver.CanStartApproach;

            float range = supportsApproach
                ? Mathf.Max(0.5f, Mathf.Max(manager.Settings.approachSelectionRange, manager.Settings.interactionRange))
                : Mathf.Max(0.5f, manager.Settings.interactionRange);

            LayerMask activeObstacleMask = overrideObstacleMask ? obstacleMaskOverride : manager.Settings.obstacleLayerMask;

            IReadOnlyList<IControlTarget> mountables = MountableRegistry.ActiveMountables;
            IControlTarget closestTarget = null;
            float closestDistance = range;

            for (int i = 0; i < mountables.Count; i++)
            {
                IControlTarget target = mountables[i];
                if (target == null || !target.IsValid) continue;

                IMountable mountable = target as IMountable ?? target.GetCapability<IMountable>();
                if (mountable == null) continue;

                Transform targetT = target.Transform;
                if (targetT == null) continue;

                MountSeat bestSeat = mountable.FindAvailableSeat(playerT.position) ?? mountable.PrimarySeat;
                Transform approachAnchor = (bestSeat != null && bestSeat.entryApproachAnchor != null)
                    ? bestSeat.entryApproachAnchor
                    : (bestSeat != null && bestSeat.seatAnchor != null ? bestSeat.seatAnchor : targetT);

                Vector3 targetPos = approachAnchor.position;
                float dist = Vector3.Distance(playerT.position, targetPos);

                CapsuleCollider playerCapsule = character.CapsuleCollider;
                bool usesBodySurface = false;

                // Only use body-surface selection when auto-approach can walk around to the entry anchor
                if (supportsApproach && target is MountableControlAdapterBase bodyTarget)
                {
                    if (bodyTarget.TryGetInteractionPoint(playerCapsule, playerT.position + Vector3.up, out Vector3 surfacePos, out float surfaceDist) &&
                        surfaceDist < dist)
                    {
                        targetPos = surfacePos;
                        dist = surfaceDist;
                        usesBodySurface = true;
                    }
                }

                if (dist <= closestDistance)
                {
                    Vector3 rayStart = usesBodySurface && playerCapsule != null && playerCapsule.enabled && playerCapsule.gameObject.activeInHierarchy
                        ? playerCapsule.bounds.center
                        : playerT.position + Vector3.up;
                    Vector3 rayEnd = usesBodySurface ? targetPos : targetPos + Vector3.up;
                    Vector3 rayDir = rayEnd - rayStart;
                    float rayDist = rayDir.magnitude;

                    bool clearLoS = true;
                    if (rayDist > 0.25f)
                    {
                        int hitCount = Physics.RaycastNonAlloc(rayStart, rayDir.normalized, losHits, rayDist, activeObstacleMask, QueryTriggerInteraction.Ignore);
                        if (hitCount >= losHits.Length) clearLoS = false;
                        for (int h = 0; h < hitCount; h++)
                        {
                            Collider hitCol = losHits[h].collider;
                            if (hitCol != null && !hitCol.isTrigger &&
                                !hitCol.transform.IsChildOf(playerT) &&
                                !hitCol.transform.IsChildOf(targetT))
                            {
                                clearLoS = false;
                                break;
                            }
                        }
                    }

                    if (clearLoS && mountable.CanMount(playerT.gameObject, out _))
                    {
                        closestDistance = dist;
                        closestTarget = target;
                    }
                }
            }

            nearbyTarget = closestTarget;
            if (nearbyTarget != null)
            {
                if (manager.Settings.allowProximityAutoEntry && !IsApproaching)
                {
                    BeginApproachOrEnter(manager, nearbyTarget);
                    return;
                }

                string enterAction = "Enter";
                IMobileControlUIProvider uiProvider = nearbyTarget.GetCapability<IMobileControlUIProvider>() ?? (nearbyTarget as IMobileControlUIProvider);
                if (uiProvider != null && !string.IsNullOrEmpty(uiProvider.MountActionLabel))
                {
                    enterAction = uiProvider.MountActionLabel;
                }
                UpdateVisuals(true, enterAction, nearbyTarget);
            }
            else
            {
                UpdateVisuals(false, string.Empty);
            }
        }

        private string FormatPrompt(string actionLabel)
        {
            return string.IsNullOrEmpty(promptFormat)
                ? actionLabel
                : string.Format(promptFormat, keyDisplayLabel, actionLabel);
        }

        private void UpdateVisuals(bool visible, string actionText, IControlTarget target = null)
        {
            if (mountIconImage != null)
            {
                IMobileControlUIProvider uiProvider = target != null
                    ? (target.GetCapability<IMobileControlUIProvider>() ?? target as IMobileControlUIProvider)
                    : null;
                Sprite icon = uiProvider != null ? uiProvider.MountIcon : null;
                if (mountIconImage.sprite != icon) mountIconImage.sprite = icon;
                mountIconImage.enabled = visible && icon != null;
            }

            // Guard BEFORE calling string.Format to guarantee zero per-tick GC allocations
            if (hasInitializedVisuals && visible == lastVisible && actionText == lastActionText)
            {
                return;
            }

            hasInitializedVisuals = true;
            lastVisible = visible;
            lastActionText = actionText;

            if (promptRoot != null) promptRoot.SetActive(visible);

            if (visible)
            {
                SetTextOnComponent(promptTextComponent, FormatPrompt(actionText));
                SetTextOnComponent(buttonLabelComponent, actionText);
            }

            if (interactionButton != null)
            {
                interactionButton.gameObject.SetActive(visible);
            }
        }

        private static void SetTextOnComponent(Component component, string value)
        {
            if (component == null) return;

            if (component is Text legacyText)
            {
                legacyText.text = value;
                return;
            }

            Type type = component.GetType();
            if (!CachedTextProperties.TryGetValue(type, out PropertyInfo textProp))
            {
                textProp = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                CachedTextProperties[type] = textProp;
            }

            if (textProp != null && textProp.CanWrite && textProp.PropertyType == typeof(string))
            {
                textProp.SetValue(component, value);
            }
        }

        private void HandleInteractionTriggered()
        {
            ExecuteInteraction();
        }

        private void OnInteractionButtonClicked()
        {
            if (MountSessionManager.TryGet(out var manager) && manager.InputRouter != null)
            {
                manager.InputRouter.TriggerInteractionFromUI();
            }
            else
            {
                ExecuteInteraction();
            }
        }

        public void ExecuteInteraction()
        {
            if (!MountSessionManager.TryGet(out var manager) || manager.IsTransitioning) return;
            if (manager.InputRouter != null && manager.InputRouter.IsSuspended) return;

            if (IsApproaching)
            {
                CancelApproach();
                EvaluateProximityAndState();
                return;
            }

            if (manager.CurrentMode == ControlMode.Mounted)
            {
                manager.RequestExit();
            }
            else
            {
                EvaluateProximityAndState();
                if (nearbyTarget != null)
                {
                    BeginApproachOrEnter(manager, nearbyTarget);
                }
            }
        }

        private void BeginApproachOrEnter(MountSessionManager manager, IControlTarget target)
        {
            CharacterControlAdapterBase character = manager.CharacterAdapter;
            if (character == null || !character.CanBoard) return;

            IMountable mount = target.GetCapability<IMountable>() ?? target as IMountable;
            if (mount == null) return;

            MountSeat seat = mount.FindAvailableSeat(character.transform.position) ?? mount.PrimarySeat;
            Transform entry = seat != null && seat.entryApproachAnchor != null ? seat.entryApproachAnchor : target.Transform;
            Vector3 entryPosition = entry != null ? entry.position : target.Transform.position;

            float stoppingDistance = Mathf.Min(
                Mathf.Max(0.5f, manager.Settings.interactionRange),
                Mathf.Max(0.2f, manager.Settings.approachStoppingDistance));

            Vector3 charPos = character.transform.position;
            approachPathResolver.Initialize(
                target.Transform,
                character.transform,
                character.CapsuleCollider,
                manager.Settings.characterCapsuleRadius,
                manager.Settings.characterCapsuleHeight,
                manager.Settings.obstacleLayerMask,
                entryPosition);

            Vector3 resolvedEntry = approachPathResolver.ResolvedDestinationWorld;
            float straightDistance = Vector3.Distance(charPos, entryPosition);
            float planarToResolved = new Vector2(resolvedEntry.x - charPos.x, resolvedEntry.z - charPos.z).magnitude;
            float maxVerticalReach = Mathf.Max(2.5f, manager.Settings.interactionRange);
            bool alreadyAtStandpoint = straightDistance <= stoppingDistance ||
                                       (planarToResolved <= stoppingDistance && Mathf.Abs(entryPosition.y - charPos.y) <= maxVerticalReach);

            ICharacterApproachDriver driver = character.GetCapability<ICharacterApproachDriver>();
            if (alreadyAtStandpoint || driver == null || !driver.CanStartApproach)
            {
                approachPathResolver.Reset();
                manager.RequestEnter(target, seat);
                return;
            }

            if (!driver.BeginApproachDrive())
            {
                approachPathResolver.Reset();
                manager.RequestEnter(target, seat);
                return;
            }

            approachTarget = target;
            approachSeat = seat;
            approachDriver = driver;
            initialManualInput = driver.ReadManualMovementInput();
            manualInputReleased = initialManualInput.sqrMagnitude < 0.04f;
            progressPosition = charPos;
            progressTime = approachStartedTime = Time.time;

            approachMaximumDistance = Mathf.Max(straightDistance + 3f, approachPathResolver.PlannedRouteLength + 4f);
        }

        private void TickApproach(float deltaTime)
        {
            if (!MountSessionManager.TryGet(out var manager) ||
                manager.CurrentMode != ControlMode.OnFoot ||
                manager.IsTransitioning ||
                (manager.InputRouter != null && manager.InputRouter.IsSuspended) ||
                Time.timeScale <= 0f ||
                manager.CharacterAdapter == null ||
                !manager.CharacterAdapter.IsValid ||
                !manager.CharacterAdapter.HasLease ||
                !manager.CharacterAdapter.CanBoard ||
                approachDriver == null ||
                !approachDriver.IsApproachStateValid ||
                approachTarget == null ||
                !approachTarget.IsValid)
            {
                CancelApproach();
                return;
            }

            Vector2 manual = approachDriver.ReadManualMovementInput();
            if (manual.sqrMagnitude < 0.04f)
            {
                manualInputReleased = true;
            }
            else if (manualInputReleased || (manual - initialManualInput).sqrMagnitude > 0.25f)
            {
                CancelApproach();
                return;
            }

            IMountable mount = approachTarget.GetCapability<IMountable>() ?? approachTarget as IMountable;
            if (mount == null || !mount.CanMount(manager.CharacterAdapter.gameObject, out _))
            {
                CancelApproach();
                return;
            }

            Transform anchor = approachSeat != null && approachSeat.entryApproachAnchor != null
                ? approachSeat.entryApproachAnchor
                : approachTarget.Transform;
            Vector3 destination = anchor != null ? anchor.position : approachTarget.Transform.position;
            Vector3 charPos = manager.CharacterAdapter.transform.position;
            Vector3 resolvedDest = approachPathResolver.ResolveDestination(charPos, destination);

            Vector3 deltaToDest = destination - charPos;
            float distance = deltaToDest.magnitude;
            float planarDistance = new Vector2(deltaToDest.x, deltaToDest.z).magnitude;
            Vector3 deltaToResolved = resolvedDest - charPos;
            float planarToResolved = new Vector2(deltaToResolved.x, deltaToResolved.z).magnitude;
            float dynamicMaxDistance = Mathf.Max(approachMaximumDistance, approachPathResolver.PlannedRouteLength + 4f);

            if (distance > dynamicMaxDistance || Time.time - approachStartedTime > 20f)
            {
                CancelApproach();
                return;
            }

            float stoppingDistance = Mathf.Min(
                Mathf.Max(0.5f, manager.Settings.interactionRange),
                Mathf.Max(0.2f, manager.Settings.approachStoppingDistance));

            float maxVerticalReach = Mathf.Max(2.5f, manager.Settings.interactionRange);
            bool verticalOk = Mathf.Abs(deltaToDest.y) <= maxVerticalReach;
            bool reachedEntry = distance <= stoppingDistance ||
                                (planarToResolved <= stoppingDistance && verticalOk) ||
                                (planarDistance <= stoppingDistance && verticalOk);

            if (reachedEntry)
            {
                IControlTarget target = approachTarget;
                MountSeat seat = approachSeat;
                CancelApproach();
                manager.RequestEnter(target, seat);
                return;
            }

            if ((charPos - progressPosition).sqrMagnitude > 0.04f)
            {
                progressPosition = charPos;
                progressTime = Time.time;
            }
            else if (Time.time - progressTime > Mathf.Max(0.5f, manager.Settings.approachStuckTimeout))
            {
                if (manager.Settings.enableDebugLogs)
                {
                    Debug.LogWarning($"[MountInteractionPrompt] Approach cancelled (stuck timeout): target={approachTarget?.TargetId}, charPos={charPos}, rawDest={destination}, resolvedDest={resolvedDest}, planarToResolved={planarToResolved:F2}m");
                }
                CancelApproach();
                return;
            }

            Vector3 nextWaypoint = approachPathResolver.ResolveNextTarget(charPos, destination);
            approachDriver.DriveApproachStep(nextWaypoint, deltaTime);
        }

        public void CancelApproach()
        {
            approachPathResolver.Reset();
            if (approachDriver != null)
            {
                approachDriver.EndApproachDrive();
                approachDriver = null;
            }
            approachTarget = null;
            approachSeat = null;
            nextDetectionTime = 0f;
        }
    }
}
