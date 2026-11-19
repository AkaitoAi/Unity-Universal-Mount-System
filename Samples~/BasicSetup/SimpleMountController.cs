using UnityEngine;

namespace AkaitoAi.UniversalMountSystem.Samples
{
    /// <summary>
    /// Minimal mount controller example (works for a vehicle, horse, hovercraft, boat, etc.).
    /// Assign this component to <c>GenericMountControlAdapter.controlBehaviours</c>.
    /// The mount system automatically disables this script when unoccupied and enables it when mounted.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class SimpleMountController : MonoBehaviour
    {
        [SerializeField] private float acceleration = 18f;
        [SerializeField] private float maxSpeed = 20f;
        [SerializeField] private float turnSpeed = 90f;

        private Rigidbody rb;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
        }

        private void FixedUpdate()
        {
            float throttle = 0f;
            float steer = 0f;
#if ENABLE_LEGACY_INPUT_MANAGER
            throttle = Input.GetAxis("Vertical");
            steer = Input.GetAxis("Horizontal");
#endif

            Vector3 currentVel = UnityCompat.GetLinearVelocity(rb);
            Vector3 planarVel = new Vector3(currentVel.x, 0f, currentVel.z);
            Vector3 targetVel = transform.forward * (throttle * maxSpeed);
            Vector3 nextPlanar = Vector3.MoveTowards(planarVel, targetVel, acceleration * Time.fixedDeltaTime);
            UnityCompat.SetLinearVelocity(rb, new Vector3(nextPlanar.x, currentVel.y, nextPlanar.z));

            if (Mathf.Abs(steer) > 0.01f)
            {
                float yaw = steer * turnSpeed * Time.fixedDeltaTime;
                rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, yaw, 0f));
            }
        }
    }
}
