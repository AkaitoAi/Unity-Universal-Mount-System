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
            float throttle = Input.GetAxis("Vertical");
            float steer = Input.GetAxis("Horizontal");

            Vector3 planarVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
            Vector3 targetVel = transform.forward * (throttle * maxSpeed);
            Vector3 nextPlanar = Vector3.MoveTowards(planarVel, targetVel, acceleration * Time.fixedDeltaTime);
            rb.velocity = new Vector3(nextPlanar.x, rb.velocity.y, nextPlanar.z);

            if (Mathf.Abs(steer) > 0.01f)
            {
                float yaw = steer * turnSpeed * Time.fixedDeltaTime;
                rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, yaw, 0f));
            }
        }
    }
}
