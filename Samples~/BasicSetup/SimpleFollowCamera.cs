using UnityEngine;

namespace AkaitoAi.UniversalMountSystem.Samples
{
    /// <summary>
    /// Minimal follow camera implementing <see cref="ICameraTargetable"/> so
    /// <see cref="Services.CameraAuthority"/> automatically retargets it when mounting or dismounting.
    /// </summary>
    public class SimpleFollowCamera : MonoBehaviour, ICameraTargetable
    {
        [SerializeField] private Transform target;
        [SerializeField] private Vector3 localOffset = new Vector3(0f, 3.5f, -6.5f);
        [SerializeField] private float positionSmooth = 8f;
        [SerializeField] private float lookSmooth = 10f;

        public void SetCameraTarget(Transform newTarget)
        {
            target = newTarget;
        }

        private void LateUpdate()
        {
            if (target == null) return;

            Vector3 desiredPos = target.TransformPoint(localOffset);
            transform.position = Vector3.Lerp(transform.position, desiredPos, 1f - Mathf.Exp(-positionSmooth * Time.deltaTime));

            Vector3 lookPoint = target.position + Vector3.up * 1.2f;
            Quaternion desiredRot = Quaternion.LookRotation((lookPoint - transform.position).normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, 1f - Mathf.Exp(-lookSmooth * Time.deltaTime));
        }
    }
}
