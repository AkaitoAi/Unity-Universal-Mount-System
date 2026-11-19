using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace AkaitoAi.UniversalMountSystem.Services
{
    /// <summary>
    /// Router managing token-based input suspension (pause, menus, death, transitions, focus loss)
    /// and unified interaction input (Legacy Input Manager keys, UI button, or New Input System callbacks).
    /// Token removal cannot clear other active suspension tokens.
    /// </summary>
    [DisallowMultipleComponent]
    public class InputAuthorityRouter : MonoBehaviour
    {
        [Header("Legacy Input Manager Bindings (Ignored When New Input System Only)")]
        [SerializeField] private KeyCode desktopInteractionKey = KeyCode.E;
        [SerializeField] private KeyCode secondaryInteractionKey = KeyCode.F;
        [SerializeField] private KeyCode gamepadInteractionKey = KeyCode.JoystickButton0;

        [Header("Policies")]
        [Tooltip("If true, automatically disables duplicate EventSystems when additive scenes load.")]
        [SerializeField] private bool reconcileDuplicateEventSystems = true;
        [Tooltip("If true, suspends input automatically when the application loses focus.")]
        [SerializeField] private bool suspendOnFocusLost = true;

        public KeyCode DesktopInteractionKey
        {
            get => desktopInteractionKey;
            set => desktopInteractionKey = value;
        }

        public KeyCode SecondaryInteractionKey
        {
            get => secondaryInteractionKey;
            set => secondaryInteractionKey = value;
        }

        public KeyCode GamepadInteractionKey
        {
            get => gamepadInteractionKey;
            set => gamepadInteractionKey = value;
        }

        private readonly HashSet<string> suspensionTokens = new HashSet<string>();
#if ENABLE_LEGACY_INPUT_MANAGER
        private bool wasInteractionPressedLastFrame;
        private bool suppressNextInteraction;
#endif
        private EventSystem activeEventSystem;

        public event Action OnInteractionTriggered;
        public event Action<bool> OnSuspensionChanged;

        public bool IsSuspended => suspensionTokens.Count > 0;
        public IReadOnlyCollection<string> ActiveTokens => suspensionTokens;

        /// <summary>
        /// Returns true if any suspension token other than <paramref name="ignoredToken"/> is active.
        /// </summary>
        public bool IsSuspendedExcluding(string ignoredToken)
        {
            if (suspensionTokens.Count == 0) return false;
            if (suspensionTokens.Count == 1 && suspensionTokens.Contains(ignoredToken)) return false;
            return true;
        }

        private void OnEnable()
        {
            if (reconcileDuplicateEventSystems)
            {
                SceneManager.sceneLoaded += HandleSceneLoaded;
            }
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private void Start()
        {
            if (reconcileDuplicateEventSystems)
            {
                ReconcileEventSystems();
            }
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (reconcileDuplicateEventSystems)
            {
                ReconcileEventSystems();
            }
        }

        private void ReconcileEventSystems()
        {
            if (activeEventSystem == null || !activeEventSystem.isActiveAndEnabled)
            {
                activeEventSystem = EventSystem.current;
            }

            EventSystem[] eventSystems = UnityCompat.FindAllObjects<EventSystem>(false);
            for (int i = 0; i < eventSystems.Length; i++)
            {
                if (!eventSystems[i].isActiveAndEnabled) continue;
                if (activeEventSystem == null) activeEventSystem = eventSystems[i];
                else if (eventSystems[i] != activeEventSystem) eventSystems[i].enabled = false;
            }

            if (activeEventSystem != null)
            {
                EventSystem.current = activeEventSystem;
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!suspendOnFocusLost) return;
            if (!hasFocus)
            {
                SuspendInput("FOCUS_LOST");
            }
            else
            {
                ResumeInput("FOCUS_LOST");
            }
        }

        public void SuspendInput(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            bool wasSuspended = IsSuspended;
            suspensionTokens.Add(token);
            if (!wasSuspended && IsSuspended)
            {
                OnSuspensionChanged?.Invoke(true);
            }
        }

        public void ResumeInput(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            bool wasSuspended = IsSuspended;
            suspensionTokens.Remove(token);
            if (wasSuspended && !IsSuspended)
            {
                OnSuspensionChanged?.Invoke(false);
            }
        }

        public void ClearSuspensions()
        {
            bool wasSuspended = IsSuspended;
            suspensionTokens.Clear();
            if (wasSuspended)
            {
                OnSuspensionChanged?.Invoke(false);
            }
        }

        public void FlushInteractionEdges()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            suppressNextInteraction = true;
            wasInteractionPressedLastFrame = true;
#endif
        }

        /// <summary>
        /// Triggers the mount/dismount interaction from a UI button or Unity New Input System action callback.
        /// </summary>
        public void TriggerInteractionFromUI()
        {
            if (IsSuspended) return;
            FlushInteractionEdges();
            OnInteractionTriggered?.Invoke();
        }

        /// <summary>
        /// Alias for <see cref="TriggerInteractionFromUI"/> for New Input System or custom input hooks.
        /// </summary>
        public void TriggerInteraction() => TriggerInteractionFromUI();

#if ENABLE_LEGACY_INPUT_MANAGER
        private void Update()
        {
            bool isPressed =
                (desktopInteractionKey != KeyCode.None && Input.GetKey(desktopInteractionKey)) ||
                (secondaryInteractionKey != KeyCode.None && Input.GetKey(secondaryInteractionKey)) ||
                (gamepadInteractionKey != KeyCode.None && Input.GetKey(gamepadInteractionKey));

            if (isPressed)
            {
                if (!wasInteractionPressedLastFrame && !suppressNextInteraction && !IsSuspended)
                {
                    OnInteractionTriggered?.Invoke();
                }
            }
            else
            {
                suppressNextInteraction = false;
            }

            wasInteractionPressedLastFrame = isPressed;
        }
#endif
    }
}
