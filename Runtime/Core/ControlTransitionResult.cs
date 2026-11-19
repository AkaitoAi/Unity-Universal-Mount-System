using System;

namespace AkaitoAi.UniversalMountSystem
{
    [Serializable]
    public struct ControlTransitionResult
    {
        public bool Success;
        public string Reason;
        public long TransactionId;
        public ControlMode TargetMode;

        public static ControlTransitionResult Succeeded(long transactionId, ControlMode mode)
        {
            return new ControlTransitionResult
            {
                Success = true,
                Reason = "Success",
                TransactionId = transactionId,
                TargetMode = mode
            };
        }

        public static ControlTransitionResult Failed(long transactionId, string reason, ControlMode mode)
        {
            return new ControlTransitionResult
            {
                Success = false,
                Reason = reason,
                TransactionId = transactionId,
                TargetMode = mode
            };
        }
    }
}
