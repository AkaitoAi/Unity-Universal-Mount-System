using System;
using System.Collections.Generic;
using UnityEngine;

namespace AkaitoAi.UniversalMountSystem.Services
{
    /// <summary>
    /// Controller-agnostic camera and audio listener coordinator.
    /// Switches cleanly between the default On-Foot rig, default Mounted rig, per-target custom
    /// <see cref="ICameraRigProvider"/> rigs (including Cinemachine 2/3 virtual cameras that share MainCamera),
    /// or named additional rigs, guaranteeing single MainCamera and AudioListener exclusivity.
    /// </summary>
    [DisallowMultipleComponent]
    public class CameraAuthority : MonoBehaviour
    {
        [Header("Default Camera Rigs")]
        [Tooltip("Default camera rig used when controlling a character on foot.")]
        [SerializeField] private ControlCameraRig onFootCameraRig = new ControlCameraRig();

        [Tooltip("Default camera rig used when mounted (used if the mount does not define its own customCameraRig).")]
        [SerializeField] private ControlCameraRig defaultMountedCameraRig = new ControlCameraRig();

        [Tooltip("Optional additional named camera rigs matched by target TargetId or activated directly.")]
        [SerializeField] private List<ControlCameraRig> additionalCameraRigs = new List<ControlCameraRig>();

        private Camera activeCamera;
        private ICameraRigAdapter activeRigAdapter;

        public Camera ActiveCamera => activeCamera != null ? activeCamera : Camera.main;
        public ICameraRigAdapter ActiveRig => activeRigAdapter;
        public ControlCameraRig OnFootCameraRig => onFootCameraRig;
        public ControlCameraRig DefaultMountedCameraRig => defaultMountedCameraRig;
        public IReadOnlyList<ControlCameraRig> AdditionalCameraRigs => additionalCameraRigs;

        private void Awake()
        {
            ResolveDefaultRigs();
        }

        public void ResolveDefaultRigs()
        {
            if (onFootCameraRig == null)
            {
                onFootCameraRig = new ControlCameraRig();
            }
            onFootCameraRig.TargetMode = ControlMode.OnFoot;
            if (string.IsNullOrEmpty(onFootCameraRig.RigName) || onFootCameraRig.RigName == "CameraRig")
            {
                onFootCameraRig.RigName = "OnFoot_CameraRig";
            }
            onFootCameraRig.ResolveDefaults();

            if (onFootCameraRig.OutputCamera == null && Camera.main != null)
            {
                onFootCameraRig.OutputCamera = Camera.main;
                onFootCameraRig.ResolveDefaults();
            }

            if (defaultMountedCameraRig == null)
            {
                defaultMountedCameraRig = new ControlCameraRig();
            }
            defaultMountedCameraRig.TargetMode = ControlMode.Mounted;
            if (string.IsNullOrEmpty(defaultMountedCameraRig.RigName) || defaultMountedCameraRig.RigName == "CameraRig")
            {
                defaultMountedCameraRig.RigName = "Mounted_CameraRig";
            }
            defaultMountedCameraRig.ResolveDefaults();

            if (additionalCameraRigs != null)
            {
                for (int i = 0; i < additionalCameraRigs.Count; i++)
                {
                    additionalCameraRigs[i]?.ResolveDefaults();
                }
            }
        }

        /// <summary>
        /// Switches camera & audio authority to any <see cref="IControlTarget"/>.
        /// Supports both separate Camera rigs and virtual cameras (e.g. Cinemachine) that share MainCamera.
        /// </summary>
        public void SwitchToTarget(IControlTarget target, ControlMode mode)
        {
            ResolveDefaultRigs();
            Transform targetTransform = target != null ? target.Transform : null;

            ICameraRigProvider rigProvider = target != null ? target.GetCapability<ICameraRigProvider>() : null;
            ICameraRigAdapter customRig = rigProvider != null ? rigProvider.CameraRig : null;
            if (customRig != null && customRig.IsConfigured)
            {
                ActivateRig(customRig, targetTransform);
                return;
            }

            if (additionalCameraRigs != null && target != null)
            {
                for (int i = 0; i < additionalCameraRigs.Count; i++)
                {
                    ControlCameraRig candidate = additionalCameraRigs[i];
                    if (candidate != null && candidate.IsConfigured &&
                        string.Equals(candidate.RigName, target.TargetId, StringComparison.OrdinalIgnoreCase))
                    {
                        ActivateRig(candidate, targetTransform);
                        return;
                    }
                }
            }

            if (mode == ControlMode.Mounted && defaultMountedCameraRig != null && defaultMountedCameraRig.IsConfigured)
            {
                ActivateRig(defaultMountedCameraRig, targetTransform);
                return;
            }

            if (onFootCameraRig != null && onFootCameraRig.IsConfigured)
            {
                ActivateRig(onFootCameraRig, targetTransform);
            }
        }

        /// <summary>
        /// Activates the given <see cref="ICameraRigAdapter"/> while deactivating all other managed rigs.
        /// If the incoming rig has no dedicated <c>OutputCamera</c> (e.g. a Cinemachine virtual camera),
        /// existing output Cameras are preserved so MainCamera is never disabled.
        /// </summary>
        public void ActivateRig(ICameraRigAdapter rig, Transform targetTransform)
        {
            if (rig == null) return;
            ResolveDefaultRigs();

            Camera incomingCam = rig.OutputCamera;

            if (activeRigAdapter != null && !ReferenceEquals(activeRigAdapter, rig))
            {
                bool deactivateOldCam = incomingCam != null && incomingCam != activeRigAdapter.OutputCamera;
                activeRigAdapter.DeactivateRig(deactivateOldCam);
            }

            if (onFootCameraRig != null && !ReferenceEquals(onFootCameraRig, rig))
            {
                bool deactivateCam = incomingCam != null && incomingCam != onFootCameraRig.OutputCamera;
                onFootCameraRig.DeactivateRig(deactivateCam);
            }

            if (defaultMountedCameraRig != null && !ReferenceEquals(defaultMountedCameraRig, rig))
            {
                bool deactivateCam = incomingCam != null && incomingCam != defaultMountedCameraRig.OutputCamera;
                defaultMountedCameraRig.DeactivateRig(deactivateCam);
            }

            if (additionalCameraRigs != null)
            {
                for (int i = 0; i < additionalCameraRigs.Count; i++)
                {
                    ControlCameraRig other = additionalCameraRigs[i];
                    if (other != null && !ReferenceEquals(other, rig))
                    {
                        bool deactivateCam = incomingCam != null && incomingCam != other.OutputCamera;
                        other.DeactivateRig(deactivateCam);
                    }
                }
            }

            rig.PrepareRig(targetTransform);
            rig.ActivateRig();

            activeRigAdapter = rig;
            if (incomingCam != null)
            {
                activeCamera = incomingCam;
            }

            VerifyExclusivity();
        }

        public Camera GetCameraForTarget(IControlTarget target, ControlMode mode)
        {
            ResolveDefaultRigs();
            if (target != null)
            {
                ICameraRigProvider provider = target.GetCapability<ICameraRigProvider>();
                if (provider != null && provider.CameraRig != null && provider.CameraRig.OutputCamera != null)
                {
                    return provider.CameraRig.OutputCamera;
                }
            }
            return GetCameraForMode(mode);
        }

        public Camera GetCameraForMode(ControlMode mode)
        {
            ResolveDefaultRigs();
            if (mode == ControlMode.Mounted && defaultMountedCameraRig != null && defaultMountedCameraRig.OutputCamera != null)
            {
                return defaultMountedCameraRig.OutputCamera;
            }
            if (onFootCameraRig != null && onFootCameraRig.OutputCamera != null)
            {
                return onFootCameraRig.OutputCamera;
            }
            return Camera.main;
        }

        /// <summary>
        /// Verifies that at most one AudioListener is active and enabled across loaded scenes.
        /// </summary>
        public bool VerifyExclusivity()
        {
            ResolveDefaultRigs();
            AudioListener[] listeners = UnityCompat.FindAllObjects<AudioListener>(false);
            AudioListener desired = activeRigAdapter != null && activeRigAdapter.AudioListener != null
                ? activeRigAdapter.AudioListener
                : (onFootCameraRig != null ? onFootCameraRig.AudioListener : null);

            int activeListeners = 0;
            for (int i = 0; i < listeners.Length; i++)
            {
                if (listeners[i] != null && listeners[i].enabled && listeners[i].gameObject.activeInHierarchy)
                {
                    activeListeners++;
                }
            }

            if (activeListeners != 1 && desired != null)
            {
                for (int i = 0; i < listeners.Length; i++)
                {
                    if (listeners[i] != null)
                    {
                        listeners[i].enabled = listeners[i] == desired;
                    }
                }
                desired.enabled = true;
                activeListeners = desired.enabled && desired.gameObject.activeInHierarchy ? 1 : 0;
            }

            return activeListeners == 1;
        }
    }
}
