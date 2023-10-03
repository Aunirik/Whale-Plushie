using UnityEngine;
using UnityEngine.Rendering;

namespace Aunirik.WhalePlushie
{
    [RequireComponent(typeof(MeshFilter))]
    public class WhalePlushieSoftbody : MonoBehaviour
    {
        [SerializeField]
        private Mesh softbodyMesh;
        [SerializeField]
        private ComputeShader deformerComputeShader;
        [Min(0.001f)]
        public float radius;
        [Min(0.001f)]
        public float springForce;
        public float deformationFactor;

        private Transform softbodyTransform;

        private Mesh mesh;
        private int meshVertexCount;
        private Vector3[] meshVertices;

        private int softbodyVertexCount;
        private Vector3[] softbodyVertices;
        private Vector3[] softbodyDisplacements;
        private Vector3 deformation;
        private SphereCollider[] colliders;
        private Rigidbody[] rigidbodies;

        private ComputeShader computeShader;
        private bool isRequestDispatched;
        private ComputeBuffer vertexComputeBuffer;
        private ComputeBuffer softbodyVertexComputeBuffer;
        private ComputeBuffer softbodyDisplacementComputeBuffer;
        private ComputeBuffer deformedVertexComputeBuffer;
        private AsyncGPUReadbackRequest request;
        private static readonly int vertexComputeBufferId = Shader.PropertyToID("VertexBuffer");
        private static readonly int softbodyVertexComputeBufferId = Shader.PropertyToID("SoftbodyVertexBuffer");
        private static readonly int softbodyDisplacementComputeBufferId = Shader.PropertyToID("SoftbodyDisplacementBuffer");
        private static readonly int deformedVertexComputeBufferId = Shader.PropertyToID("DeformedVertexBuffer");
        private static readonly int deformationFactorId = Shader.PropertyToID("DeformationFactor");
        private static readonly int vertexCountId = Shader.PropertyToID("VertexCount");
        private static readonly int softbodyVertexCountId = Shader.PropertyToID("SoftbodyVertexCount");

        public Vector3 Deformation => deformation;

        private void Awake()
        {
            MeshFilter meshFilter = GetComponent<MeshFilter>();

            this.softbodyTransform = Instantiate(new GameObject("Softbody"), transform).transform;

            this.mesh = Instantiate(meshFilter.sharedMesh);
            this.meshVertexCount = mesh.vertexCount;
            this.meshVertices = mesh.vertices;

            meshFilter.mesh = this.mesh;

            this.softbodyVertexCount = softbodyMesh.vertexCount;
            this.softbodyVertices = softbodyMesh.vertices;
            this.softbodyDisplacements = new Vector3[softbodyVertexCount];
            this.deformation = Vector3.zero;
            this.colliders = new SphereCollider[softbodyVertexCount];
            this.rigidbodies = new Rigidbody[softbodyVertexCount];

            Collider[] parentColliders = transform.GetComponentsInChildren<Collider>(true);

            GameObject softbodySpherePrefab = new GameObject("WhalePlushieSphereSoftbody");
            var softbodySphereRigibody = softbodySpherePrefab.AddComponent<Rigidbody>();
            var softbodySphereCollider = softbodySpherePrefab.AddComponent<SphereCollider>();

            softbodySphereCollider.radius = radius;
            softbodySphereRigibody.isKinematic = true;
            softbodySphereRigibody.useGravity = false;
            softbodySphereRigibody.freezeRotation = true;
            softbodySphereRigibody.mass = 0.01f;
            softbodySphereRigibody.drag = 0f;
            softbodySphereRigibody.angularDrag = 0f;

            for (int i = 0; i < softbodyVertexCount; i++)
            {
                GameObject softbodySphere = Instantiate(softbodySpherePrefab, softbodyTransform.TransformPoint(softbodyVertices[i]), Quaternion.identity, softbodyTransform);
                rigidbodies[i] = softbodySphere.GetComponent<Rigidbody>();
                colliders[i] = softbodySphere.GetComponent<SphereCollider>();
            }

            for (int i = 0; i < softbodyVertexCount; i++)
            {
                for (int j = i + 1; j < softbodyVertexCount; j++)
                {
                    Physics.IgnoreCollision(colliders[i], colliders[j], true);
                }

                foreach (Collider collider in parentColliders)
                {
                    Physics.IgnoreCollision(colliders[i], collider, true);
                }
            }

            for (int i = 0; i < softbodyVertexCount; i++)
            {
                rigidbodies[i].isKinematic = false;
            }

            this.computeShader = Instantiate(deformerComputeShader);
            this.isRequestDispatched = false;
            computeShader.SetFloat(deformationFactorId, deformationFactor);
            computeShader.SetInt(vertexCountId, meshVertexCount);
            computeShader.SetInt(softbodyVertexCountId, softbodyVertexCount);
        }

        public void OnEnable()
        {
            this.vertexComputeBuffer = new ComputeBuffer(meshVertexCount, 12);
            this.softbodyVertexComputeBuffer = new ComputeBuffer(softbodyVertexCount, 12);
            this.softbodyDisplacementComputeBuffer = new ComputeBuffer(softbodyVertexCount, 12);
            this.deformedVertexComputeBuffer = new ComputeBuffer(meshVertexCount, 12);

            vertexComputeBuffer.SetData(meshVertices);
            softbodyVertexComputeBuffer.SetData(softbodyVertices);

            computeShader.SetBuffer(0, vertexComputeBufferId, vertexComputeBuffer);
            computeShader.SetBuffer(0, softbodyVertexComputeBufferId, softbodyVertexComputeBuffer);
            computeShader.SetBuffer(0, softbodyDisplacementComputeBufferId, softbodyDisplacementComputeBuffer);
            computeShader.SetBuffer(0, deformedVertexComputeBufferId, deformedVertexComputeBuffer);
        }

        public void OnDisable()
        {
            vertexComputeBuffer.Release();
            softbodyVertexComputeBuffer.Release();
            softbodyDisplacementComputeBuffer.Release();
            deformedVertexComputeBuffer.Release();

            this.vertexComputeBuffer = null;
            this.softbodyVertexComputeBuffer = null;
            this.softbodyDisplacementComputeBuffer = null;
            this.deformedVertexComputeBuffer = null;
        }

        public void FixedUpdate()
        {
            this.deformation = Vector3.zero;

            for (int i = 0; i < softbodyVertexCount; i++)
            {
                Vector3 worldVertex = softbodyTransform.TransformPoint(softbodyVertices[i]);
                softbodyDisplacements[i] = rigidbodies[i].position - worldVertex;
                deformation += softbodyDisplacements[i];
                Vector3 springVector = Time.fixedDeltaTime * springForce * -softbodyDisplacements[i].normalized;

                if (springVector.sqrMagnitude > softbodyDisplacements[i].sqrMagnitude)
                {
                    rigidbodies[i].MovePosition(worldVertex);
                }
                else
                {
                    rigidbodies[i].MovePosition(rigidbodies[i].position + springVector);
                }
            }
        }

        public void Update()
        {
            if (isRequestDispatched)
                return;

            this.isRequestDispatched = true;
            softbodyDisplacementComputeBuffer.SetData(softbodyDisplacements);
            deformedVertexComputeBuffer.SetData(meshVertices);
            computeShader.Dispatch(0, Mathf.CeilToInt(meshVertexCount / 16.0f), 1, 1);
            this.request = AsyncGPUReadback.Request(deformedVertexComputeBuffer);
        }

        public void LateUpdate()
        {
            if (!isRequestDispatched || !request.done)
                return;

            this.isRequestDispatched = false;

            if (request.hasError)
                return;

            mesh.MarkDynamic();
            mesh.SetVertices(request.GetData<Vector3>());
        }

        public void OnValidate()
        {
            if (!enabled || colliders == null)
                return;

            for (int i = 0; i < softbodyVertexCount; i++)
            {
                colliders[i].radius = radius;
            }

            if (computeShader == null)
                return;

            computeShader.SetFloat(deformationFactorId, deformationFactor);
        }
    }
}
