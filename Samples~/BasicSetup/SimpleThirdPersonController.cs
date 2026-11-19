using UnityEngine;

namespace AkaitoAi.UniversalMountSystem.Samples
{
    /// <summary>
    /// Minimal third-person character controller example for testing the Universal Mount System.
    /// Assign this component to <c>GenericCharacterControlAdapter.controlBehaviours</c>.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class SimpleThirdPersonController : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 5.5f;
        [SerializeField] private float turnSpeed = 540f;
        [SerializeField] private float gravity = -20f;

        private CharacterController controller;
        private float verticalVelocity;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
        }

        private void Update()
        {
            float h = 0f;
            float v = 0f;
#if ENABLE_LEGACY_INPUT_MANAGER
            h = Input.GetAxis("Horizontal");
            v = Input.GetAxis("Vertical");
#endif

            Vector3 forward = Vector3.forward;
            Vector3 right = Vector3.right;
            if (Camera.main != null)
            {
                forward = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up).normalized;
                right = Vector3.ProjectOnPlane(Camera.main.transform.right, Vector3.up).normalized;
            }

            Vector3 moveDir = (forward * v + right * h);
            if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();

            if (moveDir.sqrMagnitude > 0.001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(moveDir, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRot, turnSpeed * Time.deltaTime);
            }

            if (controller.isGrounded && verticalVelocity < 0f)
            {
                verticalVelocity = -2f;
            }
            verticalVelocity += gravity * Time.deltaTime;

            Vector3 velocity = moveDir * moveSpeed + Vector3.up * verticalVelocity;
            controller.Move(velocity * Time.deltaTime);
        }
    }
}
