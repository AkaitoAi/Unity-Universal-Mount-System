using UnityEngine;

namespace AkaitoAi.UniversalMountSystem.Services
{
    /// <summary>
    /// Plans and dynamically adjusts on-foot approach paths around the target mount's
    /// colliders so the character does not get blocked by the mount it is trying to board.
    /// Zero heap allocations during FixedUpdate/Update steering ticks.
    /// </summary>
    public sealed class MountApproachPathResolver
    {
        private const float BodySkinMargin = 0.15f;
        private const float DefaultCornerClearance = 0.65f;
        private const float MaxExtraClearance = 1.35f;
        private const float WaypointReachRadius = 0.55f;
        private const float EscapeMargin = 0.12f;
        private const float ReplanCooldown = 0.35f;
        private const float StallReplanSeconds = 0.65f;
        private const float StallMovementSqrThreshold = 0.04f;
        private const float DirectionPreferenceBonus = 0.4f;

        private readonly Vector3[] corners = new Vector3[4];
        private readonly Vector3[] plannedLocalWaypoints = new Vector3[8];
        private readonly Vector3[] candidateLocalWaypoints = new Vector3[8];
        private readonly RaycastHit[] castHits = new RaycastHit[32];

        private Transform mountTransform;
        private Transform characterTransform;
        private float capsuleRadius = 0.4f;
        private float capsuleHeight = 1.8f;
        private LayerMask obstacleMask = ~0;
        private float invScaleX = 1f;
        private float invScaleZ = 1f;

        private Bounds rawLocalBounds;
        private Bounds paddedBodyBounds;
        private bool hasBodyBounds;

        private int plannedWaypointCount;
        private int currentWaypointIndex;
        private int committedDirection;
        private float extraClearance;
        private float lastReplanTime = -10f;
        private Vector3 stallCheckPosition;
        private float stallCheckTime;

        public float PlannedRouteLength { get; private set; }

        public void Initialize(
            Transform mount,
            Transform character,
            CapsuleCollider characterCapsule,
            float settingsCapsuleRadius,
            float settingsCapsuleHeight,
            LayerMask worldObstacleMask,
            Vector3 destinationWorld)
        {
            Reset();
            mountTransform = mount;
            characterTransform = character;
            obstacleMask = worldObstacleMask;

            capsuleRadius = Mathf.Max(0.1f, settingsCapsuleRadius);
            capsuleHeight = Mathf.Max(0.6f, settingsCapsuleHeight);
            if (characterCapsule != null)
            {
                Vector3 lossy = characterCapsule.transform.lossyScale;
                float radiusScale = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.z));
                capsuleRadius = Mathf.Max(capsuleRadius, characterCapsule.radius * radiusScale);
                capsuleHeight = Mathf.Max(capsuleHeight, characterCapsule.height * Mathf.Abs(lossy.y));
            }

            if (character != null)
            {
                stallCheckPosition = character.position;
            }
            stallCheckTime = Time.time;

            if (mount != null)
            {
                CacheTightLocalBounds(mount);
                if (hasBodyBounds)
                {
                    RebuildPaddedBoundsAndCorners();
                }
            }

            if (character != null)
            {
                PlanRoute(character.position, destinationWorld, 0, false);
            }
        }

        public void Reset()
        {
            mountTransform = null;
            characterTransform = null;
            hasBodyBounds = false;
            invScaleX = 1f;
            invScaleZ = 1f;
            plannedWaypointCount = 0;
            currentWaypointIndex = 0;
            committedDirection = 0;
            extraClearance = 0f;
            lastReplanTime = -10f;
            stallCheckTime = 0f;
            PlannedRouteLength = 0f;
        }

        public Vector3 ResolveNextTarget(Vector3 currentWorld, Vector3 destinationWorld)
        {
            if (!hasBodyBounds || mountTransform == null)
            {
                return destinationWorld;
            }

            Vector3 stallDelta = currentWorld - stallCheckPosition;
            stallDelta.y = 0f;
            if (stallDelta.sqrMagnitude > StallMovementSqrThreshold)
            {
                stallCheckPosition = currentWorld;
                stallCheckTime = Time.time;
            }
            else if (Time.time - stallCheckTime >= StallReplanSeconds && Time.time - lastReplanTime >= ReplanCooldown)
            {
                bool headingToFinalEntry = currentWaypointIndex >= plannedWaypointCount;
                Vector3 desiredNow = GetCurrentPlannedTargetWorld(currentWorld, destinationWorld);
                if (!IsWorldBlockingAhead(currentWorld, desiredNow) &&
                    (IsMountBlockingAhead(currentWorld, desiredNow, headingToFinalEntry, out _) || IsNearMountSurface(currentWorld)))
                {
                    int alternateDirection = committedDirection != 0 ? -committedDirection : 1;
                    extraClearance = Mathf.Min(extraClearance + 0.45f, MaxExtraClearance);
                    RebuildPaddedBoundsAndCorners();
                    PlanRoute(currentWorld, destinationWorld, alternateDirection, true);
                    lastReplanTime = Time.time;
                    stallCheckTime = Time.time;
                }
            }

            Vector3 currentLocal = mountTransform.InverseTransformPoint(currentWorld);
            Vector3 endLocal = mountTransform.InverseTransformPoint(destinationWorld);

            while (currentWaypointIndex < plannedWaypointCount)
            {
                Vector3 wpWorld = LocalToWorldAtHeight(plannedLocalWaypoints[currentWaypointIndex], currentWorld.y);
                if (PlanarDistance(currentWorld, wpWorld) <= WaypointReachRadius)
                {
                    currentWaypointIndex++;
                    continue;
                }

                if (!CrossesBounds2D(currentLocal, endLocal, paddedBodyBounds) &&
                    !IsSegmentBlockedByMount(currentWorld, destinationWorld, true, out _))
                {
                    currentWaypointIndex = plannedWaypointCount;
                    break;
                }

                break;
            }

            bool isFinalLeg = currentWaypointIndex >= plannedWaypointCount;
            Vector3 targetWorld = GetCurrentPlannedTargetWorld(currentWorld, destinationWorld);

            if (IsMountBlockingAhead(currentWorld, targetWorld, isFinalLeg, out _))
            {
                if (Time.time - lastReplanTime >= ReplanCooldown)
                {
                    if (!isFinalLeg)
                    {
                        extraClearance = Mathf.Min(extraClearance + 0.35f, MaxExtraClearance);
                        RebuildPaddedBoundsAndCorners();
                    }

                    PlanRoute(currentWorld, destinationWorld, committedDirection, true);
                    lastReplanTime = Time.time;
                    isFinalLeg = currentWaypointIndex >= plannedWaypointCount;
                    targetWorld = GetCurrentPlannedTargetWorld(currentWorld, destinationWorld);
                }

                if (IsMountBlockingAhead(currentWorld, targetWorld, isFinalLeg, out RaycastHit postReplanHit))
                {
                    return ComputeReactiveSlideTarget(currentWorld, targetWorld, postReplanHit);
                }
            }

            return targetWorld;
        }

        private Vector3 GetCurrentPlannedTargetWorld(Vector3 currentWorld, Vector3 destinationWorld)
        {
            if (currentWaypointIndex < plannedWaypointCount)
            {
                return LocalToWorldAtHeight(plannedLocalWaypoints[currentWaypointIndex], currentWorld.y);
            }

            return destinationWorld;
        }

        private void CacheTightLocalBounds(Transform mount)
        {
            hasBodyBounds = false;
            Collider[] colliders = mount.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider col = colliders[i];
                if (col == null || !col.enabled || col.isTrigger || col is WheelCollider)
                {
                    continue;
                }

                if (TryGetColliderLocalBounds(col, out Vector3 localCenter, out Vector3 localExtents))
                {
                    EncapsulateLocalBox(mount, col.transform, localCenter, localExtents);
                }
                else
                {
                    EncapsulateWorldBounds(mount, col.bounds);
                }
            }
        }

        private static bool TryGetColliderLocalBounds(Collider col, out Vector3 center, out Vector3 extents)
        {
            if (col is BoxCollider box)
            {
                center = box.center;
                extents = box.size * 0.5f;
                return true;
            }

            if (col is SphereCollider sphere)
            {
                center = sphere.center;
                float r = Mathf.Abs(sphere.radius);
                extents = new Vector3(r, r, r);
                return true;
            }

            if (col is CapsuleCollider capsule)
            {
                center = capsule.center;
                float r = Mathf.Abs(capsule.radius);
                float h = Mathf.Max(Mathf.Abs(capsule.height) * 0.5f, r);
                extents = capsule.direction == 0
                    ? new Vector3(h, r, r)
                    : capsule.direction == 1
                        ? new Vector3(r, h, r)
                        : new Vector3(r, r, h);
                return true;
            }

            if (col is MeshCollider mesh && mesh.sharedMesh != null)
            {
                Bounds mb = mesh.sharedMesh.bounds;
                center = mb.center;
                extents = mb.extents;
                return true;
            }

            center = Vector3.zero;
            extents = Vector3.zero;
            return false;
        }

        private void EncapsulateLocalBox(Transform mount, Transform colliderTransform, Vector3 localCenter, Vector3 localExtents)
        {
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 localCorner = localCenter + new Vector3(
                    (corner & 1) == 0 ? -localExtents.x : localExtents.x,
                    (corner & 2) == 0 ? -localExtents.y : localExtents.y,
                    (corner & 4) == 0 ? -localExtents.z : localExtents.z);
                Vector3 worldPoint = colliderTransform.TransformPoint(localCorner);
                Vector3 mountLocal = mount.InverseTransformPoint(worldPoint);
                EncapsulatePoint(mountLocal);
            }
        }

        private void EncapsulateWorldBounds(Transform mount, Bounds world)
        {
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 worldPoint = world.center + new Vector3(
                    (corner & 1) == 0 ? -world.extents.x : world.extents.x,
                    (corner & 2) == 0 ? -world.extents.y : world.extents.y,
                    (corner & 4) == 0 ? -world.extents.z : world.extents.z);
                Vector3 mountLocal = mount.InverseTransformPoint(worldPoint);
                EncapsulatePoint(mountLocal);
            }
        }

        private void EncapsulatePoint(Vector3 point)
        {
            if (!hasBodyBounds)
            {
                rawLocalBounds = new Bounds(point, Vector3.zero);
                hasBodyBounds = true;
            }
            else
            {
                rawLocalBounds.Encapsulate(point);
            }
        }

        private void RebuildPaddedBoundsAndCorners()
        {
            if (!hasBodyBounds || mountTransform == null)
            {
                return;
            }

            float scaleX = Mathf.Max(0.01f, Mathf.Abs(mountTransform.lossyScale.x));
            float scaleZ = Mathf.Max(0.01f, Mathf.Abs(mountTransform.lossyScale.z));
            invScaleX = 1f / scaleX;
            invScaleZ = 1f / scaleZ;

            float bodyPad = capsuleRadius + BodySkinMargin;
            paddedBodyBounds = rawLocalBounds;
            paddedBodyBounds.Expand(new Vector3((bodyPad * 2f) * invScaleX, 0f, (bodyPad * 2f) * invScaleZ));

            float cornerPad = capsuleRadius + DefaultCornerClearance + extraClearance;
            Bounds clearanceBounds = rawLocalBounds;
            clearanceBounds.Expand(new Vector3((cornerPad * 2f) * invScaleX, 0f, (cornerPad * 2f) * invScaleZ));

            Vector3 min = clearanceBounds.min;
            Vector3 max = clearanceBounds.max;
            corners[0] = new Vector3(min.x, 0f, min.z);
            corners[1] = new Vector3(min.x, 0f, max.z);
            corners[2] = new Vector3(max.x, 0f, max.z);
            corners[3] = new Vector3(max.x, 0f, min.z);
        }

        private void PlanRoute(Vector3 startWorld, Vector3 destinationWorld, int preferredDirection, bool forceDetour)
        {
            plannedWaypointCount = 0;
            currentWaypointIndex = 0;

            if (!hasBodyBounds || mountTransform == null)
            {
                committedDirection = 0;
                PlannedRouteLength = PlanarDistance(startWorld, destinationWorld);
                return;
            }

            Vector3 startLocal = mountTransform.InverseTransformPoint(startWorld);
            Vector3 endLocal = mountTransform.InverseTransformPoint(destinationWorld);

            if (!forceDetour)
            {
                bool startInsidePadded = IsInsideBounds2D(startLocal, paddedBodyBounds);
                bool crossesBody = startInsidePadded
                    ? CrossesBounds2D(startLocal, endLocal, rawLocalBounds)
                    : CrossesBounds2D(startLocal, endLocal, paddedBodyBounds);

                if (!crossesBody && !IsSegmentBlockedByMount(startWorld, destinationWorld, true, out _))
                {
                    committedDirection = 0;
                    PlannedRouteLength = PlanarDistance(startWorld, destinationWorld);
                    return;
                }
            }

            bool needsStartEscape = IsInsideBounds2D(startLocal, paddedBodyBounds);
            Vector3 routedStart = needsStartEscape
                ? ProjectOutsideBounds2D(startLocal, paddedBodyBounds, EscapeMargin)
                : startLocal;
            Vector3 routedEnd = IsInsideBounds2D(endLocal, paddedBodyBounds)
                ? ProjectOutsideBounds2D(endLocal, paddedBodyBounds, EscapeMargin)
                : endLocal;

            int bestTier = int.MaxValue;
            float bestScore = float.MaxValue;
            float bestRawLength = float.MaxValue;
            int bestDirection = 0;
            int bestCount = 0;

            for (int first = 0; first < 4; first++)
            {
                if (CrossesBounds2D(routedStart, corners[first], paddedBodyBounds))
                {
                    continue;
                }

                for (int direction = -1; direction <= 1; direction += 2)
                {
                    int candidateCount = 0;
                    if (needsStartEscape &&
                        (CrossesBounds2D(startLocal, corners[first], rawLocalBounds) ||
                         PlanarDistance(startWorld, LocalToWorldAtHeight(routedStart, startWorld.y)) > 0.2f))
                    {
                        candidateLocalWaypoints[candidateCount++] = routedStart;
                    }

                    int current = first;
                    bool reachedExit = false;
                    for (int step = 0; step < 4; step++)
                    {
                        candidateLocalWaypoints[candidateCount++] = corners[current];
                        if (!CrossesBounds2D(corners[current], routedEnd, paddedBodyBounds))
                        {
                            reachedExit = true;
                            break;
                        }

                        current = (current + direction + 4) % 4;
                    }

                    if (!reachedExit || candidateCount == 0)
                    {
                        continue;
                    }

                    EvaluateCandidateRoute(
                        startWorld,
                        destinationWorld,
                        candidateCount,
                        out float rawLength,
                        out int mountBlockedCount,
                        out int worldBlockedCount);

                    int tier = mountBlockedCount > 0 ? 2 : (worldBlockedCount > 0 ? 1 : 0);
                    float score = rawLength + (mountBlockedCount * 12f) + (worldBlockedCount * 8f);
                    if (preferredDirection != 0 && direction == preferredDirection)
                    {
                        score -= DirectionPreferenceBonus;
                    }

                    if (tier < bestTier || (tier == bestTier && score < bestScore))
                    {
                        bestTier = tier;
                        bestScore = score;
                        bestRawLength = rawLength;
                        bestDirection = direction;
                        bestCount = candidateCount;
                        for (int i = 0; i < candidateCount; i++)
                        {
                            plannedLocalWaypoints[i] = candidateLocalWaypoints[i];
                        }
                    }
                }
            }

            if (bestCount > 0)
            {
                plannedWaypointCount = bestCount;
                committedDirection = bestDirection;
                PlannedRouteLength = bestRawLength;
            }
            else if (needsStartEscape)
            {
                plannedLocalWaypoints[0] = routedStart;
                plannedWaypointCount = 1;
                committedDirection = preferredDirection != 0 ? preferredDirection : 1;
                Vector3 escapeWorld = LocalToWorldAtHeight(routedStart, startWorld.y);
                PlannedRouteLength = PlanarDistance(startWorld, escapeWorld) + PlanarDistance(escapeWorld, destinationWorld);
            }
            else
            {
                plannedWaypointCount = 0;
                committedDirection = 0;
                PlannedRouteLength = PlanarDistance(startWorld, destinationWorld);
            }
        }

        private void EvaluateCandidateRoute(
            Vector3 startWorld,
            Vector3 destinationWorld,
            int waypointCount,
            out float totalLength,
            out int mountBlockedCount,
            out int worldBlockedCount)
        {
            totalLength = 0f;
            mountBlockedCount = 0;
            worldBlockedCount = 0;

            Vector3 prev = startWorld;
            for (int i = 0; i < waypointCount; i++)
            {
                Vector3 next = LocalToWorldAtHeight(candidateLocalWaypoints[i], startWorld.y);
                totalLength += PlanarDistance(prev, next);
                if (IsSegmentBlockedByMount(prev, next, false, out _))
                {
                    mountBlockedCount++;
                }
                if (IsSegmentBlockedByWorldObstacle(prev, next))
                {
                    worldBlockedCount++;
                }
                prev = next;
            }

            totalLength += PlanarDistance(prev, destinationWorld);
            if (IsSegmentBlockedByMount(prev, destinationWorld, true, out _))
            {
                mountBlockedCount++;
            }
            if (IsSegmentBlockedByWorldObstacle(prev, destinationWorld))
            {
                worldBlockedCount++;
            }
        }

        private bool IsMountBlockingAhead(Vector3 currentWorld, Vector3 targetWorld, bool trimEndpoint, out RaycastHit closestMountHit)
        {
            Vector3 delta = targetWorld - currentWorld;
            delta.y = 0f;
            float dist = delta.magnitude;
            if (dist < 0.05f)
            {
                closestMountHit = default;
                return false;
            }

            float maxProbe = capsuleRadius + 0.85f;
            bool probeReachesTarget = dist <= maxProbe;
            float probeDist = probeReachesTarget ? dist : maxProbe;
            Vector3 probeEnd = currentWorld + (delta / dist) * probeDist;
            return IsSegmentBlockedByMount(currentWorld, probeEnd, trimEndpoint && probeReachesTarget, out closestMountHit);
        }

        private bool IsWorldBlockingAhead(Vector3 currentWorld, Vector3 targetWorld)
        {
            Vector3 delta = targetWorld - currentWorld;
            delta.y = 0f;
            float dist = delta.magnitude;
            if (dist < 0.05f)
            {
                return false;
            }

            float probeDist = Mathf.Min(dist, capsuleRadius + 0.55f);
            Vector3 probeEnd = currentWorld + (delta / dist) * probeDist;
            return IsSegmentBlockedByWorldObstacle(currentWorld, probeEnd);
        }

        private bool IsNearMountSurface(Vector3 currentWorld)
        {
            if (mountTransform == null)
            {
                return false;
            }

            Vector3 currentLocal = mountTransform.InverseTransformPoint(currentWorld);
            if (IsInsideBounds2D(currentLocal, paddedBodyBounds))
            {
                return true;
            }

            Vector3 toMount = mountTransform.position - currentWorld;
            toMount.y = 0f;
            if (toMount.sqrMagnitude > 0.01f)
            {
                Vector3 probeEnd = currentWorld + toMount.normalized * (capsuleRadius + 0.55f);
                if (IsSegmentBlockedByMount(currentWorld, probeEnd, false, out _))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsSegmentBlockedByMount(Vector3 fromWorld, Vector3 toWorld, bool trimEndpoint, out RaycastHit closestMountHit)
        {
            closestMountHit = default;
            if (mountTransform == null)
            {
                return false;
            }

            Vector3 delta = toWorld - fromWorld;
            delta.y = 0f;
            float distance = delta.magnitude;
            float probeRadius = Mathf.Max(0.15f, capsuleRadius * 0.85f);
            if (trimEndpoint)
            {
                distance -= Mathf.Max(0.15f, probeRadius);
            }
            if (distance < 0.05f)
            {
                return false;
            }

            Vector3 dir = delta.normalized;
            GetCapsuleEndpoints(fromWorld, probeRadius, out Vector3 p1, out Vector3 p2);

            int hitCount = Physics.CapsuleCastNonAlloc(p1, p2, probeRadius, dir, castHits, distance, ~0, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            bool found = false;

            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = castHits[i];
                Collider col = hit.collider;
                if (col == null || col.isTrigger || col is WheelCollider)
                {
                    continue;
                }

                if (characterTransform != null && col.transform.IsChildOf(characterTransform))
                {
                    continue;
                }

                if (!col.transform.IsChildOf(mountTransform))
                {
                    continue;
                }

                if (hit.distance < bestDist)
                {
                    bestDist = hit.distance;
                    closestMountHit = hit;
                    found = true;
                }
            }

            return found;
        }

        private bool IsSegmentBlockedByWorldObstacle(Vector3 fromWorld, Vector3 toWorld)
        {
            Vector3 delta = toWorld - fromWorld;
            delta.y = 0f;
            float distance = delta.magnitude;
            if (distance < 0.1f)
            {
                return false;
            }

            Vector3 dir = delta / distance;
            float probeRadius = Mathf.Max(0.15f, capsuleRadius * 0.85f);
            GetCapsuleEndpoints(fromWorld, probeRadius, out Vector3 p1, out Vector3 p2);

            int hitCount = Physics.CapsuleCastNonAlloc(p1, p2, probeRadius, dir, castHits, distance, obstacleMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = castHits[i];
                Collider col = hit.collider;
                if (col == null || col.isTrigger || col is WheelCollider)
                {
                    continue;
                }

                if (characterTransform != null && col.transform.IsChildOf(characterTransform))
                {
                    continue;
                }

                if (mountTransform != null && col.transform.IsChildOf(mountTransform))
                {
                    continue;
                }

                if (hit.normal.y > 0.7f)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private Vector3 ComputeReactiveSlideTarget(Vector3 currentWorld, Vector3 targetWorld, RaycastHit mountHit)
        {
            Vector3 normal = mountHit.distance > 0.001f ? mountHit.normal : Vector3.zero;
            normal.y = 0f;

            if (normal.sqrMagnitude < 0.001f)
            {
                Vector3 fromMount = currentWorld - mountTransform.position;
                fromMount.y = 0f;
                normal = fromMount.sqrMagnitude > 0.001f ? fromMount.normalized : -mountTransform.right;
            }
            else
            {
                normal.Normalize();
            }

            Vector3 tangentA = new Vector3(-normal.z, 0f, normal.x);
            Vector3 tangentB = -tangentA;

            Vector3 toTarget = targetWorld - currentWorld;
            toTarget.y = 0f;

            Vector3 chosenTangent;
            if (toTarget.sqrMagnitude > 0.01f && Mathf.Abs(Vector3.Dot(tangentA, toTarget.normalized)) > 0.15f)
            {
                chosenTangent = Vector3.Dot(tangentA, toTarget) >= 0f ? tangentA : tangentB;
            }
            else
            {
                Vector3 radial = currentWorld - mountTransform.position;
                radial.y = 0f;
                Vector3 cwTangent = new Vector3(radial.z, 0f, -radial.x);
                if (committedDirection < 0)
                {
                    cwTangent = -cwTangent;
                }
                chosenTangent = Vector3.Dot(tangentA, cwTangent) >= 0f ? tangentA : tangentB;
            }

            Vector3 slideDir = (chosenTangent + normal * 0.45f).normalized;
            Vector3 slideTarget = currentWorld + slideDir * 1.5f;
            slideTarget.y = currentWorld.y;
            return slideTarget;
        }

        private void GetCapsuleEndpoints(Vector3 worldFeetPos, float probeRadius, out Vector3 p1, out Vector3 p2)
        {
            float bottomOffset = probeRadius + 0.25f;
            float topOffset = Mathf.Max(bottomOffset + 0.1f, capsuleHeight - probeRadius);
            p1 = new Vector3(worldFeetPos.x, worldFeetPos.y + bottomOffset, worldFeetPos.z);
            p2 = new Vector3(worldFeetPos.x, worldFeetPos.y + topOffset, worldFeetPos.z);
        }

        private Vector3 LocalToWorldAtHeight(Vector3 localPoint, float worldY)
        {
            Vector3 world = mountTransform.TransformPoint(localPoint);
            world.y = worldY;
            return world;
        }

        private static float PlanarDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private Vector3 ProjectOutsideBounds2D(Vector3 point, Bounds bounds, float worldMargin)
        {
            float marginX = worldMargin * invScaleX;
            float marginZ = worldMargin * invScaleZ;
            float dMinX = Mathf.Abs(point.x - bounds.min.x);
            float dMaxX = Mathf.Abs(bounds.max.x - point.x);
            float dMinZ = Mathf.Abs(point.z - bounds.min.z);
            float dMaxZ = Mathf.Abs(bounds.max.z - point.z);

            Vector3 result = point;
            result.y = 0f;

            float minDist = dMinX;
            int face = 0;
            if (dMaxX < minDist) { minDist = dMaxX; face = 1; }
            if (dMinZ < minDist) { minDist = dMinZ; face = 2; }
            if (dMaxZ < minDist) { face = 3; }

            switch (face)
            {
                case 0: result.x = bounds.min.x - marginX; break;
                case 1: result.x = bounds.max.x + marginX; break;
                case 2: result.z = bounds.min.z - marginZ; break;
                default: result.z = bounds.max.z + marginZ; break;
            }

            return result;
        }

        private bool IsInsideBounds2D(Vector3 point, Bounds bounds)
        {
            float epsX = 0.05f * invScaleX;
            float epsZ = 0.05f * invScaleZ;
            return point.x > bounds.min.x + epsX && point.x < bounds.max.x - epsX &&
                   point.z > bounds.min.z + epsZ && point.z < bounds.max.z - epsZ;
        }

        private bool CrossesBounds2D(Vector3 start, Vector3 end, Bounds bodyBounds)
        {
            float epsX = 0.05f * invScaleX;
            float epsZ = 0.05f * invScaleZ;
            Vector3 min = bodyBounds.min + new Vector3(epsX, 0f, epsZ);
            Vector3 max = bodyBounds.max - new Vector3(epsX, 0f, epsZ);
            float enter = 0f;
            float exit = 1f;
            return ClipAxis(start.x, end.x - start.x, min.x, max.x, ref enter, ref exit) &&
                   ClipAxis(start.z, end.z - start.z, min.z, max.z, ref enter, ref exit);
        }

        private static bool ClipAxis(float start, float delta, float min, float max, ref float enter, ref float exit)
        {
            if (Mathf.Abs(delta) < 0.0001f)
            {
                return start > min && start < max;
            }

            float a = (min - start) / delta;
            float b = (max - start) / delta;
            if (a > b)
            {
                float swap = a;
                a = b;
                b = swap;
            }

            enter = Mathf.Max(enter, a);
            exit = Mathf.Min(exit, b);
            return enter < exit;
        }
    }
}
