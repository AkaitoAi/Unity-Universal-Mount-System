using System.Collections.Generic;
using UnityEngine;

namespace AkaitoAi.UniversalMountSystem.Services
{
    /// <summary>
    /// Resolves the active simulation origin (Activity Anchor) and protected transforms
    /// for world streaming, distance culling, AI, or traffic systems.
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldRelevanceResolver : MonoBehaviour
    {
        private Transform activeActivityAnchor;
        private readonly List<Transform> protectedAnchors = new List<Transform>();

        public Transform ActiveActivityAnchor => activeActivityAnchor;
        public IReadOnlyList<Transform> ProtectedAnchors => protectedAnchors;

        public void UpdateRelevance(ControlContext context)
        {
            protectedAnchors.Clear();

            if (context.Body != null)
            {
                protectedAnchors.Add(context.Body.transform);
            }

            if (context.ControlledTarget != null && context.ControlledTarget.IsValid)
            {
                if (!protectedAnchors.Contains(context.ControlledTarget.Transform))
                {
                    protectedAnchors.Add(context.ControlledTarget.Transform);
                }
            }

            activeActivityAnchor = context.ActivityAnchor;
            if (activeActivityAnchor == null && context.Body != null)
            {
                activeActivityAnchor = context.Body.transform;
            }
        }

        public bool IsProtected(Transform candidate)
        {
            if (candidate == null) return false;
            for (int i = 0; i < protectedAnchors.Count; i++)
            {
                Transform p = protectedAnchors[i];
                if (p == null) continue;
                if (candidate == p || candidate.IsChildOf(p) || p.IsChildOf(candidate))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
