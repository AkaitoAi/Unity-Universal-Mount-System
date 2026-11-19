using System;
using System.Collections.Generic;
using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// Pure C# single-authority coordinator for local player control session transitions and leases.
    /// Manages state transitions, seat switching, lease ownership tracking, rollbacks, and versioned
    /// context snapshots independently of any character controller or mount implementation.
    /// </summary>
    public class ControlSessionCoordinator
    {
        private ControlContext currentContext = ControlContext.Empty;
        private TransitionPhase phase = TransitionPhase.Ready;
        private long sessionGeneration = 1;
        private long contextVersion = 1;
        private long transactionCounter = 100;
        private long activeTransactionId;

        private long activeLeaseId;
        private IControlTarget activeLeaseTarget;

        private IControlTarget prospectiveTarget;
        private MountSeat prospectiveSeat;
        private IMountable prospectiveMountable;

        public ControlContext CurrentContext => currentContext;
        public TransitionPhase Phase => phase;
        public bool IsTransitioning => phase != TransitionPhase.Ready;
        public long ActiveTransactionId => activeTransactionId;
        public long ActiveLeaseId => activeLeaseId;
        public long SessionGeneration => sessionGeneration;
        public long ContextVersion => contextVersion;

        /// <summary>
        /// Optional predicate to gate whether SetInputActive(true) is permitted.
        /// Prevents coordinator handoffs from bypassing pause, death, focus loss, or other active suspension tokens.
        /// </summary>
        public Func<bool> IsInputAllowed { get; set; }

        public event Action<ControlContext, ControlContext, string> OnContextCommitted;
        public event Action<long, string, ControlMode> OnTransitionFailed;

        private void SetTargetInputActive(IControlTarget target, bool active)
        {
            if (target == null || !target.IsValid) return;
            if (active)
            {
                target.SetInputActive(IsInputAllowed == null || IsInputAllowed());
            }
            else
            {
                target.SetInputActive(false);
            }
        }

        public void Initialize(string playerId, GameObject body, IControlTarget bodyTarget, Camera initialCamera, Transform activityAnchor = null)
        {
            if (body == null && bodyTarget == null)
            {
                throw new ArgumentNullException(nameof(bodyTarget), "Either body or bodyTarget must be provided to initialize session.");
            }

            if (currentContext.IsValid && currentContext.Body == body && currentContext.ControlledTarget == bodyTarget &&
                activeLeaseTarget == bodyTarget && activeLeaseId != 0 && currentContext.Mode == ControlMode.OnFoot)
            {
                return;
            }

            if (activeLeaseTarget != null && activeLeaseTarget.IsValid && activeLeaseId != 0)
            {
                activeLeaseTarget.NeutralizeInputs();
                activeLeaseTarget.ReleaseControlLease(activeLeaseId);
            }

            sessionGeneration++;
            contextVersion++;
            phase = TransitionPhase.Ready;
            activeTransactionId = 0;
            prospectiveTarget = null;
            prospectiveSeat = null;
            prospectiveMountable = null;

            long initialLeaseId = ++transactionCounter;
            if (bodyTarget != null)
            {
                bodyTarget.AcquireControlLease(initialLeaseId);
                SetTargetInputActive(bodyTarget, true);
            }
            activeLeaseId = initialLeaseId;
            activeLeaseTarget = bodyTarget;

            Transform anchor = activityAnchor != null
                ? activityAnchor
                : (body != null ? body.transform : (bodyTarget != null ? bodyTarget.Transform : null));

            var protectedList = new List<Transform>();
            if (body != null)
            {
                protectedList.Add(body.transform);
            }

            ControlContext previous = currentContext;
            currentContext = new ControlContext(
                contextVersion,
                sessionGeneration,
                playerId ?? "LocalPlayer",
                body,
                bodyTarget,
                ControlMode.OnFoot,
                null,
                initialCamera,
                anchor,
                protectedList
            );

            OnContextCommitted?.Invoke(previous, currentContext, "Initialized");
        }

        public ControlTransitionResult RequestTransitionToMounted(
            IControlTarget mountTarget,
            MountSeat targetSeat,
            Camera mountCamera,
            Func<bool> prepareCallback,
            Action commitCallback,
            Action rollbackCallback)
        {
            if (phase != TransitionPhase.Ready)
            {
                string busyReason = $"Coordinator busy in phase: {phase}";
                OnTransitionFailed?.Invoke(0, busyReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, busyReason, ControlMode.Mounted);
            }

            if (currentContext.Mode != ControlMode.OnFoot)
            {
                string modeReason = $"Cannot mount: current mode is {currentContext.Mode}, expected OnFoot";
                OnTransitionFailed?.Invoke(0, modeReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, modeReason, ControlMode.Mounted);
            }

            if (!currentContext.IsValid)
            {
                string invalidBodyReason = "Current control context body is invalid or null";
                OnTransitionFailed?.Invoke(0, invalidBodyReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, invalidBodyReason, ControlMode.Mounted);
            }

            if (mountTarget == null || !mountTarget.IsValid)
            {
                string targetReason = "Target mount is invalid or null";
                OnTransitionFailed?.Invoke(0, targetReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, targetReason, ControlMode.Mounted);
            }

            IMountable mountable = mountTarget.GetCapability<IMountable>();
            if (mountable == null)
            {
                string noMountReason = "Target does not support IMountable";
                OnTransitionFailed?.Invoke(0, noMountReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, noMountReason, ControlMode.Mounted);
            }

            if (!mountable.CanMount(currentContext.Body, out string mountCheckReason))
            {
                string rejectReason = $"Mount rejected: {mountCheckReason}";
                OnTransitionFailed?.Invoke(0, rejectReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, rejectReason, ControlMode.Mounted);
            }

            MountSeat seat = targetSeat ?? mountable.FindAvailableSeat(
                currentContext.Body != null ? currentContext.Body.transform.position : mountTarget.Transform.position) ?? mountable.PrimarySeat;
            if (seat == null)
            {
                string noSeatReason = "No eligible mount seat available";
                OnTransitionFailed?.Invoke(0, noSeatReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, noSeatReason, ControlMode.Mounted);
            }

            long currentSessionGeneration = sessionGeneration;
            long txId = ++transactionCounter;
            activeTransactionId = txId;

            prospectiveTarget = seat.grantsControlLease ? mountTarget : null;
            prospectiveSeat = seat;
            prospectiveMountable = mountable;

            phase = TransitionPhase.Validating;

            if (!mountable.ReserveSeat(seat, currentContext.Body, txId))
            {
                CleanupProspectiveReservations(txId);
                phase = TransitionPhase.Ready;
                activeTransactionId = 0;
                string reserveFail = "Failed to reserve seat";
                OnTransitionFailed?.Invoke(txId, reserveFail, ControlMode.Mounted);
                return ControlTransitionResult.Failed(txId, reserveFail, ControlMode.Mounted);
            }

            if (seat.grantsControlLease && !mountTarget.AcquireControlLease(txId))
            {
                CleanupProspectiveReservations(txId);
                phase = TransitionPhase.Ready;
                activeTransactionId = 0;
                string leaseFail = "Failed to acquire target control lease";
                OnTransitionFailed?.Invoke(txId, leaseFail, ControlMode.Mounted);
                return ControlTransitionResult.Failed(txId, leaseFail, ControlMode.Mounted);
            }

            phase = TransitionPhase.Reserved;

            phase = TransitionPhase.Quiesced;
            IControlTarget sourceTarget = currentContext.ControlledTarget;
            if (sourceTarget != null && sourceTarget.IsValid)
            {
                sourceTarget.NeutralizeInputs();
                sourceTarget.SetInputActive(false);
            }

            phase = TransitionPhase.Preparing;
            bool prepared;
            try
            {
                prepared = prepareCallback == null || prepareCallback();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ControlSessionCoordinator] Prepare callback exception: {ex}");
                prepared = false;
            }

            if (sessionGeneration != currentSessionGeneration)
            {
                Debug.LogWarning("[ControlSessionCoordinator] Session generation changed during prepare callback; aborting transition.");
                prepared = false;
            }

            if (!prepared)
            {
                phase = TransitionPhase.RollingBack;
                try { rollbackCallback?.Invoke(); }
                catch (Exception ex) { Debug.LogError($"[ControlSessionCoordinator] Rollback callback exception: {ex}"); }

                CleanupProspectiveReservations(txId);
                if (sourceTarget != null && sourceTarget.IsValid) SetTargetInputActive(sourceTarget, true);

                phase = TransitionPhase.Ready;
                activeTransactionId = 0;
                string prepFail = "Preparation failed during mount transition";
                OnTransitionFailed?.Invoke(txId, prepFail, ControlMode.Mounted);
                return ControlTransitionResult.Failed(txId, prepFail, ControlMode.Mounted);
            }

            phase = TransitionPhase.Committing;
            bool commitSucceeded;
            try
            {
                commitCallback?.Invoke();
                commitSucceeded = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ControlSessionCoordinator] Commit callback exception: {ex}");
                commitSucceeded = false;
            }

            if (!commitSucceeded)
            {
                phase = TransitionPhase.RollingBack;
                try { rollbackCallback?.Invoke(); }
                catch (Exception ex) { Debug.LogError($"[ControlSessionCoordinator] Rollback callback exception after commit failure: {ex}"); }

                CleanupProspectiveReservations(txId);
                if (sourceTarget != null && sourceTarget.IsValid) SetTargetInputActive(sourceTarget, true);

                phase = TransitionPhase.Ready;
                activeTransactionId = 0;
                string commitFail = "Commit callback threw exception; transition rolled back";
                OnTransitionFailed?.Invoke(txId, commitFail, ControlMode.Mounted);
                return ControlTransitionResult.Failed(txId, commitFail, ControlMode.Mounted);
            }

            mountable.OccupySeat(seat, currentContext.Body);

            if (sourceTarget != null && sourceTarget.IsValid && activeLeaseId != 0)
            {
                sourceTarget.ReleaseControlLease(activeLeaseId);
            }

            if (seat.grantsControlLease)
            {
                activeLeaseId = txId;
                activeLeaseTarget = mountTarget;
                SetTargetInputActive(mountTarget, true);
            }
            else
            {
                activeLeaseId = 0;
                activeLeaseTarget = null;
            }

            prospectiveTarget = null;
            prospectiveSeat = null;
            prospectiveMountable = null;

            ControlContext previous = currentContext;
            contextVersion++;
            var protectedAnchors = new List<Transform>();
            if (currentContext.Body != null) protectedAnchors.Add(currentContext.Body.transform);
            if (mountTarget != null && mountTarget.Transform != null) protectedAnchors.Add(mountTarget.Transform);

            currentContext = new ControlContext(
                contextVersion,
                sessionGeneration,
                previous.PlayerId,
                previous.Body,
                mountTarget,
                ControlMode.Mounted,
                seat,
                mountCamera != null ? mountCamera : previous.ActiveCamera,
                mountTarget.Transform,
                protectedAnchors
            );

            phase = TransitionPhase.Ready;
            activeTransactionId = 0;

            OnContextCommitted?.Invoke(previous, currentContext, "Mounted");
            return ControlTransitionResult.Succeeded(txId, ControlMode.Mounted);
        }

        /// <summary>
        /// Switches seats within the currently occupied mount (e.g. Passenger to Driver or Driver to Gunner).
        /// </summary>
        public ControlTransitionResult RequestSwitchSeat(
            MountSeat targetSeat,
            Func<bool> prepareCallback,
            Action commitCallback,
            Action rollbackCallback)
        {
            if (phase != TransitionPhase.Ready)
            {
                string busyReason = $"Coordinator busy in phase: {phase}";
                OnTransitionFailed?.Invoke(0, busyReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, busyReason, ControlMode.Mounted);
            }

            if (currentContext.Mode != ControlMode.Mounted || currentContext.ControlledTarget == null || !currentContext.ControlledTarget.IsValid)
            {
                string modeReason = "Cannot switch seat when not mounted";
                OnTransitionFailed?.Invoke(0, modeReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, modeReason, ControlMode.Mounted);
            }

            MountSeat currentSeat = currentContext.OccupiedSeat;
            if (targetSeat == null || targetSeat == currentSeat)
            {
                string sameReason = "Target seat is null or already occupied by this character";
                OnTransitionFailed?.Invoke(0, sameReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, sameReason, ControlMode.Mounted);
            }

            IControlTarget mountTarget = currentContext.ControlledTarget;
            IMountable mountable = mountTarget.GetCapability<IMountable>();
            if (mountable == null)
            {
                string noMountReason = "Active mount does not support IMountable";
                OnTransitionFailed?.Invoke(0, noMountReason, ControlMode.Mounted);
                return ControlTransitionResult.Failed(0, noMountReason, ControlMode.Mounted);
            }

            long txId = ++transactionCounter;
            activeTransactionId = txId;
            prospectiveSeat = targetSeat;
            prospectiveMountable = mountable;
            phase = TransitionPhase.Validating;

            if (!mountable.ReserveSeat(targetSeat, currentContext.Body, txId))
            {
                CleanupProspectiveReservations(txId);
                phase = TransitionPhase.Ready;
                activeTransactionId = 0;
                string reserveFail = "Target seat is occupied or unavailable";
                OnTransitionFailed?.Invoke(txId, reserveFail, ControlMode.Mounted);
                return ControlTransitionResult.Failed(txId, reserveFail, ControlMode.Mounted);
            }

            bool needsNewLease = targetSeat.grantsControlLease && activeLeaseTarget != mountTarget;
            if (needsNewLease)
            {
                prospectiveTarget = mountTarget;
                if (!mountTarget.AcquireControlLease(txId))
                {
                    CleanupProspectiveReservations(txId);
                    phase = TransitionPhase.Ready;
                    activeTransactionId = 0;
                    string leaseFail = "Failed to acquire control lease for target seat";
                    OnTransitionFailed?.Invoke(txId, leaseFail, ControlMode.Mounted);
                    return ControlTransitionResult.Failed(txId, leaseFail, ControlMode.Mounted);
                }
            }

            phase = TransitionPhase.Preparing;
            bool prepared;
            try { prepared = prepareCallback == null || prepareCallback(); }
            catch (Exception ex) { Debug.LogError($"[ControlSessionCoordinator] Seat switch prepare exception: {ex}"); prepared = false; }

            if (!prepared)
            {
                phase = TransitionPhase.RollingBack;
                try { rollbackCallback?.Invoke(); } catch (Exception ex) { Debug.LogError(ex); }
                CleanupProspectiveReservations(txId);
                phase = TransitionPhase.Ready;
                activeTransactionId = 0;
                string prepFail = "Preparation failed during seat switch";
                OnTransitionFailed?.Invoke(txId, prepFail, ControlMode.Mounted);
                return ControlTransitionResult.Failed(txId, prepFail, ControlMode.Mounted);
            }

            phase = TransitionPhase.Committing;
            try { commitCallback?.Invoke(); }
            catch (Exception ex)
            {
                Debug.LogError($"[ControlSessionCoordinator] Seat switch commit exception: {ex}");
                phase = TransitionPhase.RollingBack;
                try { rollbackCallback?.Invoke(); } catch (Exception rEx) { Debug.LogError(rEx); }
                CleanupProspectiveReservations(txId);
                phase = TransitionPhase.Ready;
                activeTransactionId = 0;
                string commitFail = "Commit failed during seat switch";
                OnTransitionFailed?.Invoke(txId, commitFail, ControlMode.Mounted);
                return ControlTransitionResult.Failed(txId, commitFail, ControlMode.Mounted);
            }

            if (currentSeat != null) mountable.VacateSeat(currentSeat, currentContext.Body);
            mountable.OccupySeat(targetSeat, currentContext.Body);

            if (!targetSeat.grantsControlLease && activeLeaseTarget == mountTarget && activeLeaseId != 0)
            {
                mountTarget.NeutralizeInputs();
                mountTarget.SetInputActive(false);
                mountTarget.ReleaseControlLease(activeLeaseId);
                activeLeaseId = 0;
                activeLeaseTarget = null;
            }
            else if (needsNewLease)
            {
                activeLeaseId = txId;
                activeLeaseTarget = mountTarget;
                SetTargetInputActive(mountTarget, true);
            }

            prospectiveTarget = null;
            prospectiveSeat = null;
            prospectiveMountable = null;

            ControlContext previous = currentContext;
            contextVersion++;
            currentContext = new ControlContext(
                contextVersion,
                sessionGeneration,
                previous.PlayerId,
                previous.Body,
                mountTarget,
                ControlMode.Mounted,
                targetSeat,
                previous.ActiveCamera,
                mountTarget.Transform,
                previous.ProtectedAnchors
            );

            phase = TransitionPhase.Ready;
            activeTransactionId = 0;

            OnContextCommitted?.Invoke(previous, currentContext, "SeatSwitched");
            return ControlTransitionResult.Succeeded(txId, ControlMode.Mounted);
        }

        public ControlTransitionResult RequestTransitionToOnFoot(
            IControlTarget bodyTarget,
            Vector3 exitWorldPosition,
            Quaternion exitWorldRotation,
            Camera characterCamera,
            Func<bool> prepareCallback,
            Action commitCallback,
            Action rollbackCallback)
        {
            if (phase != TransitionPhase.Ready)
            {
                string busyReason = $"Coordinator busy in phase: {phase}";
                OnTransitionFailed?.Invoke(0, busyReason, ControlMode.OnFoot);
                return ControlTransitionResult.Failed(0, busyReason, ControlMode.OnFoot);
            }

            if (currentContext.Mode != ControlMode.Mounted)
            {
                string modeReason = $"Cannot exit: current mode is {currentContext.Mode}, expected Mounted";
                OnTransitionFailed?.Invoke(0, modeReason, ControlMode.OnFoot);
                return ControlTransitionResult.Failed(0, modeReason, ControlMode.OnFoot);
            }

            if (!currentContext.IsValid)
            {
                string invalidBodyReason = "Current control context body is invalid or null";
                OnTransitionFailed?.Invoke(0, invalidBodyReason, ControlMode.OnFoot);
                return ControlTransitionResult.Failed(0, invalidBodyReason, ControlMode.OnFoot);
            }

            IControlTarget currentMount = currentContext.ControlledTarget;
            MountSeat currentSeat = currentContext.OccupiedSeat;
            IMountable mountable = currentMount != null ? currentMount.GetCapability<IMountable>() : null;

            if (mountable != null && !mountable.CanExit(currentContext.Body, out string exitRejectReason))
            {
                string exitFail = $"Exit rejected by mount: {exitRejectReason}";
                OnTransitionFailed?.Invoke(0, exitFail, ControlMode.OnFoot);
                return ControlTransitionResult.Failed(0, exitFail, ControlMode.OnFoot);
            }

            if (bodyTarget == null || !bodyTarget.IsValid)
            {
                string bodyFail = "Body target is invalid or null";
                OnTransitionFailed?.Invoke(0, bodyFail, ControlMode.OnFoot);
                return ControlTransitionResult.Failed(0, bodyFail, ControlMode.OnFoot);
            }

            long currentSessionGeneration = sessionGeneration;
            long txId = ++transactionCounter;
            activeTransactionId = txId;

            prospectiveTarget = bodyTarget;
            prospectiveSeat = null;
            prospectiveMountable = null;

            phase = TransitionPhase.Validating;

            if (!bodyTarget.AcquireControlLease(txId))
            {
                CleanupProspectiveReservations(txId);
                phase = TransitionPhase.Ready;
                activeTransactionId = 0;
                string leaseFail = "Failed to acquire body control lease";
                OnTransitionFailed?.Invoke(txId, leaseFail, ControlMode.OnFoot);
                return ControlTransitionResult.Failed(txId, leaseFail, ControlMode.OnFoot);
            }

            phase = TransitionPhase.Reserved;

            phase = TransitionPhase.Quiesced;
            bool hadMountLease = currentMount != null && currentMount.IsValid && activeLeaseTarget == currentMount && activeLeaseId != 0;
            if (hadMountLease)
            {
                currentMount.NeutralizeInputs();
                currentMount.SetInputActive(false);
            }

            phase = TransitionPhase.Preparing;
            bool prepared;
            try { prepared = prepareCallback == null || prepareCallback(); }
            catch (Exception ex) { Debug.LogError($"[ControlSessionCoordinator] Prepare callback exception: {ex}"); prepared = false; }

            if (sessionGeneration != currentSessionGeneration)
            {
                Debug.LogWarning("[ControlSessionCoordinator] Session generation changed during exit prepare callback; aborting transition.");
                prepared = false;
            }

            if (!prepared)
            {
                phase = TransitionPhase.RollingBack;
                try { rollbackCallback?.Invoke(); } catch (Exception ex) { Debug.LogError(ex); }

                CleanupProspectiveReservations(txId);
                if (hadMountLease) SetTargetInputActive(currentMount, true);

                phase = TransitionPhase.Ready;
                activeTransactionId = 0;
                string prepFail = "Preparation failed during exit transition";
                OnTransitionFailed?.Invoke(txId, prepFail, ControlMode.OnFoot);
                return ControlTransitionResult.Failed(txId, prepFail, ControlMode.OnFoot);
            }

            phase = TransitionPhase.Committing;
            bool commitSucceeded;
            try
            {
                commitCallback?.Invoke();
                commitSucceeded = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ControlSessionCoordinator] Commit callback exception: {ex}");
                commitSucceeded = false;
            }

            if (!commitSucceeded)
            {
                phase = TransitionPhase.RollingBack;
                try { rollbackCallback?.Invoke(); } catch (Exception ex) { Debug.LogError(ex); }

                CleanupProspectiveReservations(txId);
                if (hadMountLease) SetTargetInputActive(currentMount, true);

                phase = TransitionPhase.Ready;
                activeTransactionId = 0;
                string commitFail = "Commit callback threw exception; exit transition rolled back";
                OnTransitionFailed?.Invoke(txId, commitFail, ControlMode.OnFoot);
                return ControlTransitionResult.Failed(txId, commitFail, ControlMode.OnFoot);
            }

            if (mountable != null && currentSeat != null)
            {
                mountable.VacateSeat(currentSeat, currentContext.Body);
            }

            if (hadMountLease)
            {
                currentMount.ReleaseControlLease(activeLeaseId);
            }

            activeLeaseId = txId;
            activeLeaseTarget = bodyTarget;
            prospectiveTarget = null;
            prospectiveSeat = null;
            prospectiveMountable = null;

            SetTargetInputActive(bodyTarget, true);

            ControlContext previous = currentContext;
            contextVersion++;
            var protectedAnchors = new List<Transform>();
            if (currentContext.Body != null) protectedAnchors.Add(currentContext.Body.transform);

            Transform onFootAnchor = previous.Body != null ? previous.Body.transform : bodyTarget.Transform;

            currentContext = new ControlContext(
                contextVersion,
                sessionGeneration,
                previous.PlayerId,
                previous.Body,
                bodyTarget,
                ControlMode.OnFoot,
                null,
                characterCamera != null ? characterCamera : previous.ActiveCamera,
                onFootAnchor,
                protectedAnchors
            );

            phase = TransitionPhase.Ready;
            activeTransactionId = 0;

            OnContextCommitted?.Invoke(previous, currentContext, "ExitedToOnFoot");
            return ControlTransitionResult.Succeeded(txId, ControlMode.OnFoot);
        }

        public void CancelActiveTransition(string reason)
        {
            if (phase == TransitionPhase.Ready) return;

            long txId = activeTransactionId;
            phase = TransitionPhase.RollingBack;

            CleanupProspectiveReservations(txId);

            if (activeLeaseTarget != null && activeLeaseTarget.IsValid)
            {
                SetTargetInputActive(activeLeaseTarget, true);
            }

            phase = TransitionPhase.Ready;
            activeTransactionId = 0;

            OnTransitionFailed?.Invoke(txId, $"Transition cancelled: {reason}", currentContext.Mode);
        }

        private void CleanupProspectiveReservations(long txId)
        {
            if (prospectiveSeat != null && prospectiveMountable != null)
            {
                try { prospectiveMountable.ReleaseSeatReservation(prospectiveSeat, txId); }
                catch (Exception ex) { Debug.LogWarning($"[ControlSessionCoordinator] Exception releasing seat reservation: {ex}"); }
                prospectiveSeat = null;
                prospectiveMountable = null;
            }

            if (prospectiveTarget != null)
            {
                try { prospectiveTarget.ReleaseControlLease(txId); }
                catch (Exception ex) { Debug.LogWarning($"[ControlSessionCoordinator] Exception releasing lease: {ex}"); }
                prospectiveTarget = null;
            }
        }

        /// <summary>
        /// Safe recovery to on-foot mode following an unexpected mount invalidation, destruction, or character death.
        /// Vacates any occupied seat and releases any active mount lease.
        /// </summary>
        public void RecoverToOnFoot(Vector3 safePosition, Quaternion safeRotation, IControlTarget bodyTarget, Camera characterCamera, bool repositionBody = true)
        {
            phase = TransitionPhase.Recovering;
            sessionGeneration++;
            contextVersion++;
            activeTransactionId = 0;
            CleanupProspectiveReservations(0);

            // Vacate occupied seat if mount still exists
            if (currentContext.Mode == ControlMode.Mounted && currentContext.ControlledTarget != null && currentContext.OccupiedSeat != null)
            {
                if (!(currentContext.ControlledTarget is UnityEngine.Object uo && uo == null))
                {
                    IMountable mountable = currentContext.ControlledTarget.GetCapability<IMountable>();
                    mountable?.VacateSeat(currentContext.OccupiedSeat, currentContext.Body);
                }
            }

            if (activeLeaseTarget != null && activeLeaseTarget != bodyTarget && activeLeaseTarget.IsValid && activeLeaseId != 0)
            {
                activeLeaseTarget.NeutralizeInputs();
                activeLeaseTarget.ReleaseControlLease(activeLeaseId);
            }

            if (repositionBody && currentContext.Body != null)
            {
                currentContext.Body.transform.SetPositionAndRotation(safePosition, safeRotation);
            }

            long newLeaseId = ++transactionCounter;
            if (bodyTarget != null && bodyTarget.IsValid)
            {
                bodyTarget.AcquireControlLease(newLeaseId);
                SetTargetInputActive(bodyTarget, true);
            }
            activeLeaseId = bodyTarget != null ? newLeaseId : 0;
            activeLeaseTarget = bodyTarget;

            var protectedAnchors = currentContext.Body != null
                ? new List<Transform> { currentContext.Body.transform }
                : new List<Transform>();

            ControlContext previous = currentContext;
            currentContext = new ControlContext(
                contextVersion,
                sessionGeneration,
                previous.PlayerId ?? "LocalPlayer",
                previous.Body,
                bodyTarget,
                ControlMode.OnFoot,
                null,
                characterCamera,
                previous.Body != null ? previous.Body.transform : null,
                protectedAnchors
            );

            phase = TransitionPhase.Ready;
            OnContextCommitted?.Invoke(previous, currentContext, "RecoveredToOnFoot");
        }

        public void Shutdown()
        {
            if (activeTransactionId != 0)
            {
                CancelActiveTransition("Shutdown");
            }

            if (activeLeaseTarget != null && activeLeaseTarget.IsValid && activeLeaseId != 0)
            {
                activeLeaseTarget.NeutralizeInputs();
                activeLeaseTarget.ReleaseControlLease(activeLeaseId);
            }

            activeLeaseId = 0;
            activeLeaseTarget = null;
            activeTransactionId = 0;
            prospectiveTarget = null;
            prospectiveSeat = null;
            prospectiveMountable = null;
            phase = TransitionPhase.Ready;
            currentContext = ControlContext.Empty;
        }
    }
}
