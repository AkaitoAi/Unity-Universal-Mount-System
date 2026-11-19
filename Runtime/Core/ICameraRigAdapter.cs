using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// Contract for adapting camera rigs (Cinemachine 2/3, third-person cameras, vehicle cameras, or custom scripts)
    /// to a unified camera authority.
    /// </summary>
    public interface ICameraRigAdapter
    {
        string RigName { get; }
        bool IsConfigured { get; }
        Camera OutputCamera { get; }
        AudioListener AudioListener { get; }
        void PrepareRig(Transform target);
        void ActivateRig();
        void DeactivateRig(bool deactivateOutputCamera = true);
    }

    /// <summary>
    /// Optional interface that any camera script (MonoBehaviour) can implement to receive the active control target Transform generically.
    /// </summary>
    public interface ICameraTargetable
    {
        void SetCameraTarget(Transform target);
    }

    /// <summary>
    /// Capability interface allowing an <see cref="IControlTarget"/> to supply its own camera rig adapter.
    /// </summary>
    public interface ICameraRigProvider : IControlCapability
    {
        ICameraRigAdapter CameraRig { get; }
    }

    /// <summary>
    /// Capability interface for control targets that supply dedicated mobile/contextual UI roots, icons, and action labels.
    /// </summary>
    public interface IMobileControlUIProvider : IControlCapability
    {
        GameObject MobileControlsRoot { get; }
        IReadOnlyList<GameObject> ActiveUiRoots { get; }
        string MountActionLabel { get; }
        string ExitActionLabel { get; }
        Sprite MountIcon { get; }
    }

    /// <summary>
    /// Serializable, vendor-agnostic camera rig definition wrapping a generic Camera, AudioListener,
    /// camera controller script (Behaviour — including Cinemachine Virtual/Camera), and optional AudioSources.
    /// </summary>
    [Serializable]
    public class ControlCameraRig : ICameraRigAdapter
    {
        [SerializeField] private string rigName = "CameraRig";
        [SerializeField] private ControlMode targetMode = ControlMode.Mounted;
        [SerializeField] private GameObject rigRoot;
        [SerializeField] private Camera outputCamera;
        [SerializeField] private AudioListener audioListener;
        [Tooltip("Generic camera controller script (e.g. CinemachineVirtualCamera, CinemachineCamera, third-person camera, or custom follow script).")]
        [SerializeField] private Behaviour cameraScript;
        [Tooltip("Optional extra behaviours on the camera rig to toggle with the rig.")]
        [SerializeField] private Behaviour[] extraBehaviours;
        [Tooltip("Optional AudioSources on the camera rig to toggle with the rig.")]
        [SerializeField] private AudioSource[] audioSources;

        private Action<Transform> customPrepare;
        private Action customActivate;
        private Action customDeactivate;

        private static readonly Dictionary<Type, MemberInfo> CachedTargetMembers = new Dictionary<Type, MemberInfo>();
        private static readonly Dictionary<Type, PropertyInfo> CachedLookAtProperties = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> CachedCm3TargetProperties = new Dictionary<Type, PropertyInfo>();

        public string RigName
        {
            get => string.IsNullOrEmpty(rigName)
                ? (cameraScript != null ? cameraScript.GetType().Name : (outputCamera != null ? outputCamera.name : "CameraRig"))
                : rigName;
            set => rigName = value;
        }

        public ControlMode TargetMode
        {
            get => targetMode;
            set => targetMode = value;
        }

        public GameObject RigRoot
        {
            get => rigRoot;
            set => rigRoot = value;
        }

        public Camera OutputCamera
        {
            get => outputCamera;
            set => outputCamera = value;
        }

        public AudioListener AudioListener
        {
            get => audioListener;
            set => audioListener = value;
        }

        public Behaviour CameraScript
        {
            get => cameraScript;
            set => cameraScript = value;
        }

        public Behaviour[] ExtraBehaviours
        {
            get => extraBehaviours;
            set => extraBehaviours = value;
        }

        public AudioSource[] AudioSources
        {
            get => audioSources;
            set => audioSources = value;
        }

        public bool IsConfigured => outputCamera != null || cameraScript != null || rigRoot != null;

        public ControlCameraRig() { }

        public ControlCameraRig(
            string name,
            ControlMode mode,
            GameObject root,
            Camera camera,
            AudioListener listener,
            Behaviour script = null,
            Action<Transform> prepare = null,
            Action activate = null,
            Action deactivate = null)
        {
            rigName = name;
            targetMode = mode;
            rigRoot = root;
            outputCamera = camera;
            audioListener = listener;
            cameraScript = script;
            customPrepare = prepare;
            customActivate = activate;
            customDeactivate = deactivate;
        }

        public void Configure(
            string name,
            GameObject root,
            Camera camera,
            AudioListener listener,
            Behaviour script = null,
            Behaviour[] extra = null,
            AudioSource[] sources = null)
        {
            rigName = name;
            rigRoot = root;
            outputCamera = camera;
            audioListener = listener;
            cameraScript = script;
            if (extra != null) extraBehaviours = extra;
            if (sources != null) audioSources = sources;
            ResolveDefaults();
        }

        public void SetCustomHooks(Action<Transform> prepare, Action activate, Action deactivate)
        {
            customPrepare = prepare;
            customActivate = activate;
            customDeactivate = deactivate;
        }

        public void ResolveDefaults()
        {
            if (outputCamera == null && cameraScript != null)
            {
                outputCamera = cameraScript.GetComponent<Camera>() ?? cameraScript.GetComponentInChildren<Camera>(true);
            }
            if (outputCamera == null && rigRoot != null)
            {
                outputCamera = rigRoot.GetComponent<Camera>() ?? rigRoot.GetComponentInChildren<Camera>(true);
            }
            if (rigRoot == null && cameraScript != null)
            {
                rigRoot = cameraScript.gameObject;
            }
            if (rigRoot == null && outputCamera != null)
            {
                rigRoot = outputCamera.gameObject;
            }
            if (audioListener == null && outputCamera != null)
            {
                audioListener = outputCamera.GetComponent<AudioListener>() ?? outputCamera.GetComponentInChildren<AudioListener>(true);
            }
            if (audioListener == null && rigRoot != null)
            {
                audioListener = rigRoot.GetComponentInChildren<AudioListener>(true);
            }
        }

        public void PrepareRig(Transform target)
        {
            ResolveDefaults();
            if (customPrepare != null)
            {
                customPrepare.Invoke(target);
                return;
            }
            BindTargetToCameraScript(cameraScript, target);
        }

        public void ActivateRig()
        {
            ResolveDefaults();
            if (rigRoot != null && !rigRoot.activeSelf)
            {
                rigRoot.SetActive(true);
            }
            if (outputCamera != null)
            {
                if (!outputCamera.gameObject.activeSelf)
                {
                    outputCamera.gameObject.SetActive(true);
                }
                outputCamera.enabled = true;
                outputCamera.tag = "MainCamera";
            }
            if (cameraScript != null)
            {
                if (!cameraScript.gameObject.activeSelf)
                {
                    cameraScript.gameObject.SetActive(true);
                }
                cameraScript.enabled = true;
            }
            if (extraBehaviours != null)
            {
                for (int i = 0; i < extraBehaviours.Length; i++)
                {
                    if (extraBehaviours[i] != null) extraBehaviours[i].enabled = true;
                }
            }
            if (audioListener != null)
            {
                audioListener.enabled = true;
            }
            if (audioSources != null)
            {
                for (int i = 0; i < audioSources.Length; i++)
                {
                    if (audioSources[i] != null)
                    {
                        audioSources[i].enabled = true;
                        audioSources[i].mute = false;
                    }
                }
            }
            customActivate?.Invoke();
        }

        public void DeactivateRig(bool deactivateOutputCamera = true)
        {
            customDeactivate?.Invoke();

            if (cameraScript != null)
            {
                cameraScript.enabled = false;
            }
            if (extraBehaviours != null)
            {
                for (int i = 0; i < extraBehaviours.Length; i++)
                {
                    if (extraBehaviours[i] != null) extraBehaviours[i].enabled = false;
                }
            }
            if (audioSources != null)
            {
                for (int i = 0; i < audioSources.Length; i++)
                {
                    if (audioSources[i] != null)
                    {
                        audioSources[i].mute = true;
                    }
                }
            }
            if (deactivateOutputCamera)
            {
                if (audioListener != null)
                {
                    audioListener.enabled = false;
                }
                if (outputCamera != null)
                {
                    if (outputCamera.CompareTag("MainCamera"))
                    {
                        outputCamera.tag = "Untagged";
                    }
                    outputCamera.enabled = false;
                }
            }
        }

        /// <summary>
        /// Generic helper to bind a target Transform to any camera script using <see cref="ICameraTargetable"/>
        /// or cached reflection (supports Cinemachine 2 Follow/LookAt, Cinemachine 3 Target.TrackingTarget,
        /// SetCameraTarget, SetMainTarget, SetTarget, target, followTarget).
        /// </summary>
        public static bool BindTargetToCameraScript(Behaviour script, Transform target)
        {
            if (script == null || target == null) return false;

            if (script is ICameraTargetable targetable)
            {
                targetable.SetCameraTarget(target);
                return true;
            }

            Type type = script.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            if (!CachedTargetMembers.TryGetValue(type, out MemberInfo member))
            {
                member = (MemberInfo)type.GetMethod("SetCameraTarget", flags, null, new[] { typeof(Transform) }, null)
                      ?? (MemberInfo)type.GetMethod("SetMainTarget", flags, null, new[] { typeof(Transform) }, null)
                      ?? (MemberInfo)type.GetMethod("SetTarget", flags, null, new[] { typeof(Transform) }, null)
                      ?? (MemberInfo)type.GetProperty("Follow", flags)
                      ?? (MemberInfo)type.GetProperty("target", flags)
                      ?? (MemberInfo)type.GetProperty("Target", flags)
                      ?? (MemberInfo)type.GetField("target", flags)
                      ?? (MemberInfo)type.GetField("Target", flags)
                      ?? (MemberInfo)type.GetField("followTarget", flags);
                CachedTargetMembers[type] = member;
            }

            bool bound = false;

            if (member is MethodInfo method)
            {
                method.Invoke(script, new object[] { target });
                bound = true;
            }
            else if (member is PropertyInfo prop && prop.CanWrite)
            {
                if (typeof(Transform).IsAssignableFrom(prop.PropertyType))
                {
                    prop.SetValue(script, target);
                    bound = true;

                    // Also update Cinemachine 2 LookAt if present
                    if (prop.Name == "Follow")
                    {
                        if (!CachedLookAtProperties.TryGetValue(type, out PropertyInfo lookAtProp))
                        {
                            lookAtProp = type.GetProperty("LookAt", flags);
                            CachedLookAtProperties[type] = lookAtProp;
                        }
                        if (lookAtProp != null && lookAtProp.CanWrite && typeof(Transform).IsAssignableFrom(lookAtProp.PropertyType))
                        {
                            lookAtProp.SetValue(script, target);
                        }
                    }
                }
                else if (prop.Name == "Target" && prop.PropertyType.IsValueType)
                {
                    // Unity 6 Cinemachine 3: CameraTarget struct with TrackingTarget field
                    object targetStruct = prop.GetValue(script);
                    if (targetStruct != null)
                    {
                        FieldInfo trackingField = prop.PropertyType.GetField("TrackingTarget", BindingFlags.Instance | BindingFlags.Public);
                        if (trackingField != null && typeof(Transform).IsAssignableFrom(trackingField.FieldType))
                        {
                            trackingField.SetValue(targetStruct, target);
                            prop.SetValue(script, targetStruct);
                            bound = true;
                        }
                    }
                }
            }
            else if (member is FieldInfo field && typeof(Transform).IsAssignableFrom(field.FieldType))
            {
                field.SetValue(script, target);
                bound = true;
            }

            return bound;
        }
    }

    /// <summary>
    /// Delegate-backed implementation of <see cref="ICameraRigAdapter"/> for code-driven rig lifecycle control.
    /// </summary>
    public class GenericCameraRigAdapter : ICameraRigAdapter
    {
        public string RigName { get; }
        public bool IsConfigured => OutputCamera != null || onActivate != null;
        public Camera OutputCamera { get; }
        public AudioListener AudioListener { get; }

        private readonly Action<Transform> onPrepare;
        private readonly Action onActivate;
        private readonly Action onDeactivate;

        public GenericCameraRigAdapter(
            string rigName,
            Camera outputCamera,
            AudioListener audioListener,
            Action<Transform> prepare = null,
            Action activate = null,
            Action deactivate = null)
        {
            RigName = rigName;
            OutputCamera = outputCamera;
            AudioListener = audioListener;
            onPrepare = prepare;
            onActivate = activate;
            onDeactivate = deactivate;
        }

        public void PrepareRig(Transform target) => onPrepare?.Invoke(target);
        public void ActivateRig() => onActivate?.Invoke();
        public void DeactivateRig(bool deactivateOutputCamera = true) => onDeactivate?.Invoke();
    }
}
