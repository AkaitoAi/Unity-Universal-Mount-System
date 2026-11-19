using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace AkaitoAi.UniversalMountSystem.Services
{
    /// <summary>
    /// Router managing token-based input suspension (pause, menus, death, transitions, focus loss)
    /// and unified interaction input (keyboard, gamepad, or UI button).
    /// Token removal cannot clear other active suspension tokens.
    /// </summary>
    [DisallowMultipleComponent]
    public class InputAuthorityRouter : MonoBehaviour
    {
        [Header("Interaction Key Bindings")]
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
        private bool wasInteractionPressedLastFrame;
        private bool suppressNextInteraction;
        private EventSystem activeEventSystem;

        public event Action OnInteractionTriggered;
        public event Action<bool> OnSuspensionChanged;

        public bool IsSuspended => suspensionTokens.Count > 0;
        public IReadOnlyCollection<string> ActiveTokens => suspensionTokens;

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

            EventSystem[] eventSystems = FindObjectsOfType<EventSystem>();
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
            suppressNextInteraction = true;
            wasInteractionPressedLastFrame = true;
        }

        /// <summary>
        /// Triggers the mount/dismount interaction from a UI button or custom Input System callback.
        /// </summary>
        public void TriggerInteractionFromUI()
        {
            if (IsSuspended) return;
            FlushInteractionEdges();
            OnInteractionTriggered?.Invoke();
        }

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
    }
}
