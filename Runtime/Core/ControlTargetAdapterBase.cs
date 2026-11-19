using System;
using System.Collections.Generic;
using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// MonoBehaviour base class providing lease management, capability caching, lifetime validation,
    /// and generic controller script, AudioSource, camera rig, and mobile/contextual UI bindings.
    /// </summary>
    public abstract class ControlTargetAdapterBase : MonoBehaviour, IControlTarget, ICameraRigProvider, IMobileControlUIProvider
    {
        [SerializeField] private string targetId;

        [Header("Generic Control Bindings (Optional)")]
        [Tooltip("Controller scripts (MonoBehaviours/Behaviours) to enable when controlled and disable when uncontrolled.")]
        [SerializeField] protected Behaviour[] controlBehaviours;

        [Tooltip("AudioSources to enable/unmute when actively controlled and mute/stop when uncontrolled.")]
        [SerializeField] protected AudioSource[] controlledAudioSources;

        [Tooltip("If true, controlledAudioSources are stopped when losing control; otherwise they are muted.")]
        [SerializeField] protected bool stopAudioWhenUncontrolled;

        [Header("UI & Camera Rig (Optional)")]
        [Tooltip("Dedicated mobile/contextual touch controls UI root for this controller. If unassigned, falls back to the mode default UI.")]
        [SerializeField] protected GameObject mobileControlsRoot;

        [Tooltip("Additional HUD or UI roots to activate only while this target is actively controlled.")]
        [SerializeField] protected GameObject[] activeUiRoots;

        [Tooltip("Optional custom camera rig (Camera, AudioListener, Camera Script, AudioSources) used when this target is active.")]
        [SerializeField] protected ControlCameraRig customCameraRig = new ControlCameraRig();

        private long activeLeaseTransactionId;
        private readonly List<IControlCapability> cachedCapabilities = new List<IControlCapability>();
        private bool capabilitiesCached;

        public virtual string TargetId
        {
            get
            {
                if (string.IsNullOrEmpty(targetId))
                {
                    targetId = gameObject.name;
                }
                return targetId;
            }
        }

        public GameObject TargetGameObject => this != null ? gameObject : null;
        public Transform Transform => this != null ? transform : null;
        public virtual bool IsValid => this != null && gameObject != null && enabled && gameObject.activeInHierarchy;
        public bool HasLease => activeLeaseTransactionId != 0;
        public long ActiveLeaseTransactionId => activeLeaseTransactionId;

        public GameObject MobileControlsRoot
        {
            get => mobileControlsRoot;
            set => mobileControlsRoot = value;
        }

        public IReadOnlyList<GameObject> ActiveUiRoots => activeUiRoots ?? Array.Empty<GameObject>();
        public virtual string MountActionLabel => "Enter";
        public virtual string ExitActionLabel => "Exit";

        public ControlCameraRig CustomCameraRig => customCameraRig;
        public virtual ICameraRigAdapter CameraRig => (customCameraRig != null && customCameraRig.IsConfigured) ? customCameraRig : null;

        protected virtual void Awake()
        {
            CacheCapabilitiesIfNeeded();
        }

        public void ConfigureControlBindings(
            Behaviour[] behaviours,
            AudioSource[] audioSources = null,
            GameObject mobileUiRoot = null,
            GameObject[] uiRoots = null)
        {
            controlBehaviours = behaviours;
            if (audioSources != null) controlledAudioSources = audioSources;
            if (mobileUiRoot != null) mobileControlsRoot = mobileUiRoot;
            if (uiRoots != null) activeUiRoots = uiRoots;
        }

        public void CacheCapabilitiesIfNeeded()
        {
            if (capabilitiesCached) return;
            cachedCapabilities.Clear();
            GetComponents(cachedCapabilities);
            capabilitiesCached = true;
        }

        public virtual bool AcquireControlLease(long transactionId)
        {
            if (transactionId <= 0)
            {
                return false;
            }
            if (activeLeaseTransactionId != 0 && activeLeaseTransactionId != transactionId)
            {
                return false;
            }
            activeLeaseTransactionId = transactionId;
            OnLeaseAcquired(transactionId);
            return true;
        }

        public virtual void ReleaseControlLease(long transactionId)
        {
            if (transactionId == 0 || activeLeaseTransactionId == transactionId)
            {
                long releasedId = activeLeaseTransactionId;
                activeLeaseTransactionId = 0;
                OnLeaseReleased(releasedId);
            }
            else
            {
                Debug.LogWarning($"[ControlTargetAdapterBase] Rejecting lease release on {TargetId}: held lease is {activeLeaseTransactionId}, requested release was {transactionId}");
            }
        }

        public virtual T GetCapability<T>() where T : class, IControlCapability
        {
            if (this is T selfCap)
            {
                return selfCap;
            }
            CacheCapabilitiesIfNeeded();
            for (int i = 0; i < cachedCapabilities.Count; i++)
            {
                if (cachedCapabilities[i] is T cap)
                {
                    return cap;
                }
            }
            return null;
        }

        /// <summary>
        /// Applies generic controller script, AudioSource, and UI root states when control is activated or deactivated.
        /// </summary>
        protected virtual void ApplyGenericControlState(bool active)
        {
            if (controlBehaviours != null)
            {
                for (int i = 0; i < controlBehaviours.Length; i++)
                {
                    if (controlBehaviours[i] != null && controlBehaviours[i] != this)
                    {
                        controlBehaviours[i].enabled = active;
                    }
                }
            }

            if (controlledAudioSources != null)
            {
                for (int i = 0; i < controlledAudioSources.Length; i++)
                {
                    AudioSource src = controlledAudioSources[i];
                    if (src == null) continue;

                    if (active)
                    {
                        src.enabled = true;
                        src.mute = false;
                        if (!src.isPlaying && src.playOnAwake && src.gameObject.activeInHierarchy)
                        {
                            src.Play();
                        }
                    }
                    else
                    {
                        src.mute = true;
                        if (stopAudioWhenUncontrolled && src.isPlaying)
                        {
                            src.Stop();
                        }
                    }
                }
            }

            if (activeUiRoots != null)
            {
                for (int i = 0; i < activeUiRoots.Length; i++)
                {
                    if (activeUiRoots[i] != null)
                    {
                        activeUiRoots[i].SetActive(active);
                    }
                }
            }
        }

        public abstract void NeutralizeInputs();
        public abstract void SetInputActive(bool active);

        protected virtual void OnLeaseAcquired(long transactionId) { }
        protected virtual void OnLeaseReleased(long transactionId) { }

        protected virtual void OnEnable()
        {
            if (this is IMountable || GetCapability<IMountable>() != null)
            {
                MountableRegistry.Register(this);
            }
        }

        protected virtual void OnDisable()
        {
            NeutralizeInputs();
            MountableRegistry.Unregister(this);
        }

        protected virtual void OnDestroy()
        {
            activeLeaseTransactionId = 0;
            cachedCapabilities.Clear();
            MountableRegistry.Unregister(this);
        }
    }
}
