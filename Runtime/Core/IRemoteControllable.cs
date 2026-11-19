namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// Extension seam capability for remote-controlled targets (drones, turrets, robots).
    /// </summary>
    public interface IRemoteControllable : IControlCapability
    {
        bool IsLinkActive { get; }
        float MaxRange { get; }
        void DisconnectFailsafe();
    }
}
