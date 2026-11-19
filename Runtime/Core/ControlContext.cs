using System;
using System.Collections.Generic;
using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// Versioned, immutable snapshot describing the local control session state.
    /// Broadcast only on committed transitions. Never reconstructed per-frame.
    /// </summary>
    [Serializable]
    public struct ControlContext
    {
        public long ContextVersion { get; }
        public long SessionGeneration { get; }
        public string PlayerId { get; }
        public GameObject Body { get; }
        public IControlTarget ControlledTarget { get; }
        public ControlMode Mode { get; }
        public MountSeat OccupiedSeat { get; }
        public Camera ActiveCamera { get; }
        public Transform ActivityAnchor { get; }
        public IReadOnlyList<Transform> ProtectedAnchors { get; }

        public bool IsValid => Body != null || ControlledTarget != null;

        public ControlContext(
            long contextVersion,
            long sessionGeneration,
            string playerId,
            GameObject body,
            IControlTarget controlledTarget,
            ControlMode mode,
            MountSeat occupiedSeat,
            Camera activeCamera,
            Transform activityAnchor,
            IReadOnlyList<Transform> protectedAnchors)
        {
            ContextVersion = contextVersion;
            SessionGeneration = sessionGeneration;
            PlayerId = playerId ?? string.Empty;
            Body = body;
            ControlledTarget = controlledTarget;
            Mode = mode;
            OccupiedSeat = occupiedSeat;
            ActiveCamera = activeCamera;
            ActivityAnchor = activityAnchor;
            ProtectedAnchors = protectedAnchors ?? Array.Empty<Transform>();
        }

        public static ControlContext Empty => new ControlContext(
            0,
            0,
            string.Empty,
            null,
            null,
            ControlMode.OnFoot,
            null,
            null,
            null,
            Array.Empty<Transform>()
        );
    }
}
