namespace AkaitoAi.UniversalMountSystem
{
    public enum ControlMode
    {
        OnFoot,
        Mounted,
        Remote
    }

    public enum TransitionPhase
    {
        Ready,
        Validating,
        Reserved,
        Quiesced,
        Preparing,
        Committing,
        RollingBack,
        Recovering
    }
}
