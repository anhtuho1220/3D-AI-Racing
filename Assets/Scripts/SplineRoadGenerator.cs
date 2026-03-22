using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;


[ExecuteInEditMode]
[RequireComponent(typeof(SplineContainer), typeof(MeshRenderer), typeof(MeshFilter))]
[RequireComponent(typeof(MeshCollider))]
public class SplineRoadGenerator : MonoBehaviour
{
    [SerializeField]
    private SplineContainer m_SplineContainer;

    [SerializeField]
    private float m_Width = 10f;
    
    [SerializeField]
    private float m_Resolution = 1f; // Segments per meter

    [SerializeField]
    private float m_TextureScale = 1f;
    
    [SerializeField]
    private Material m_RoadMaterial;

    [SerializeField]
    private bool m_GenerateWalls = true;

    [SerializeField]
    private Material m_WallMaterial;

    [SerializeField]
    private float m_WallHeight = 1f;

    [SerializeField]
    private float m_WallThickness = 0.5f;

    [SerializeField]
    private float m_WallOffset = 0f;

    [SerializeField]
    private bool m_Optimize = true;

    [SerializeField]
    private float m_OptimizeAngleThreshold = 2.0f;

    // Support for variable width if needed in the future, but keeping it simple for now with a float multiplier or AnimationCurve/SplineData
    // For now, let's stick to a simple width multiplier, but if we want variable width like LoftRoadBehaviour, we can add SplineData later.
    // RandomTrackGenerator uses a constant width, so a float is sufficient and faster.
    
    private Mesh m_Mesh;
    private List<Vector3> m_Positions = new List<Vector3>();
    private List<Vector3> m_Normals = new List<Vector3>();
    private List<Vector2> m_UVs = new List<Vector2>();
    private List<int> m_Indices = new List<int>();
    private List<int> m_WallIndices = new List<int>();
    private bool m_RebuildRequested = false;

    public float Width
    {
        get => m_Width;
        set
        {
            if (Math.Abs(m_Width - value) > 0.001f)
            {
                m_Width = value;
                m_RebuildRequested = true;
            }
        }
    }

    public float Resolution
    {
        get => m_Resolution;
        set
        {
            if (Math.Abs(m_Resolution - value) > 0.001f)
            {
                m_Resolution = value;
                m_RebuildRequested = true;
            }
        }
    }

    public float TextureScale
    {
        get => m_TextureScale;
        set
        {
            if (Math.Abs(m_TextureScale - value) > 0.001f)
            {
                m_TextureScale = value;
                m_RebuildRequested = true;
            }
        }
    }

    public Material RoadMaterial
    {
        get => m_RoadMaterial;
        set
        {
            if (m_RoadMaterial != value)
            {
                m_RoadMaterial = value;
                GetComponent<MeshRenderer>().sharedMaterial = m_RoadMaterial;
            }
        }
    }

    public bool GenerateWalls { get => m_GenerateWalls; set { if (m_GenerateWalls != value) { m_GenerateWalls = value; m_RebuildRequested = true; } } }
    public Material WallMaterial { get => m_WallMaterial; set { if (m_WallMaterial != value) { m_WallMaterial = value; m_RebuildRequested = true; } } }
    public float WallHeight { get => m_WallHeight; set { if (m_WallHeight != value) { m_WallHeight = value; m_RebuildRequested = true; } } }
    public float WallThickness { get => m_WallThickness; set { if (m_WallThickness != value) { m_WallThickness = value; m_RebuildRequested = true; } } }
    public float WallOffset { get => m_WallOffset; set { if (m_WallOffset != value) { m_WallOffset = value; m_RebuildRequested = true; } } }

    public bool Optimize
    {
        get => m_Optimize;
        set
        {
            if (m_Optimize != value)
            {
                m_Optimize = value;
                m_RebuildRequested = true;
            }
        }
    }

    public float OptimizeAngleThreshold
    {
        get => m_OptimizeAngleThreshold;
        set
        {
            if (Math.Abs(m_OptimizeAngleThreshold - value) > 0.001f)
            {
                m_OptimizeAngleThreshold = value;
                m_RebuildRequested = true;
            }
        }
    }

    public SplineContainer Container
    {
        get => m_SplineContainer;
        set
        {
            if (m_SplineContainer != value)
            {
                Unsubscribe();
                m_SplineContainer = value;
                Subscribe();
                m_RebuildRequested = true;
            }
        }
    }

    private void OnEnable()
    {
        if (m_SplineContainer == null)
            m_SplineContainer = GetComponent<SplineContainer>();

        Subscribe();
        m_RebuildRequested = true;
    }

    private void OnDisable()
    {
        Unsubscribe();
        if (m_Mesh != null)
        {
            if (Application.isPlaying) Destroy(m_Mesh);
            else DestroyImmediate(m_Mesh);
        }
    }

    private void Subscribe()
    {
        if (m_SplineContainer != null)
        {
            Spline.Changed += OnSplineChanged;
            SplineContainer.SplineAdded += OnSplineContainerAdded;
            SplineContainer.SplineRemoved += OnSplineContainerRemoved;
            SplineContainer.SplineReordered += OnSplineContainerReordered;
        }
    }

    private void Unsubscribe()
    {
        Spline.Changed -= OnSplineChanged;
        SplineContainer.SplineAdded -= OnSplineContainerAdded;
        SplineContainer.SplineRemoved -= OnSplineContainerRemoved;
        SplineContainer.SplineReordered -= OnSplineContainerReordered;
    }

    private void OnSplineChanged(Spline spline, int index, SplineModification modification)
    {
        if (m_SplineContainer != null)
        {
            foreach (var s in m_SplineContainer.Splines)
            {
                if (s == spline)
                {
                    m_RebuildRequested = true;
                    break;
                }
            }
        }
    }

    private void OnSplineContainerAdded(SplineContainer container, int index)
    {
        if (container == m_SplineContainer) m_RebuildRequested = true;
    }

    private void OnSplineContainerRemoved(SplineContainer container, int index)
    {
        if (container == m_SplineContainer) m_RebuildRequested = true;
    }

    private void OnSplineContainerReordered(SplineContainer container, int index1, int index2)
    {
        if (container == m_SplineContainer) m_RebuildRequested = true;
    }

    private void Update()
    {
        if (m_RebuildRequested)
        {
            Rebuild();
        }
    }

    public void Rebuild()
    {
        m_RebuildRequested = false;

        if (m_SplineContainer == null || m_SplineContainer.Spline == null) return;

        if (m_Mesh == null)
        {
            m_Mesh = new Mesh();
            m_Mesh.name = "SplineRoadMesh";
            GetComponent<MeshFilter>().sharedMesh = m_Mesh;
        }

        // Apply material if not set
        var meshRenderer = GetComponent<MeshRenderer>();
        if (m_GenerateWalls && m_WallMaterial != null)
        {
            if (meshRenderer.sharedMaterials.Length != 2 || meshRenderer.sharedMaterials[0] != m_RoadMaterial || meshRenderer.sharedMaterials[1] != m_WallMaterial)
            {
                meshRenderer.sharedMaterials = new Material[] { m_RoadMaterial, m_WallMaterial };
            }
        }
        else
        {
            if (meshRenderer.sharedMaterials.Length != 1 || meshRenderer.sharedMaterials[0] != m_RoadMaterial)
            {
                meshRenderer.sharedMaterials = new Material[] { m_RoadMaterial };
            }
        }

        m_Mesh.Clear();
        m_Positions.Clear();
        m_Normals.Clear();
        m_UVs.Clear();
        m_Indices.Clear();
        m_WallIndices.Clear();

        Spline spline = m_SplineContainer.Spline;
        float length = spline.GetLength();
        if (length <= 0.001f) return;

        // Calculate steps based on resolution
        float segmentsPerUnit = Mathf.Clamp(m_Resolution, 0.1f, 50f);
        int totalSegments = Mathf.CeilToInt(length * segmentsPerUnit);


        float stepSize = 1f / totalSegments;

        List<RoadSample> samples = new List<RoadSample>(totalSegments + 1);

        for (int i = 0; i <= totalSegments; i++)
        {
            // Evaluate spline at T
            float t = (i == totalSegments) ? 1f : i * stepSize;

            if (spline.Closed && i == totalSegments)
            {
                // Ensure perfect closure position-wise for the last point
                t = 0f;
            }

            SplineUtility.Evaluate(spline, i == totalSegments && spline.Closed ? 0f : t, out float3 pos, out float3 dir, out float3 up);

            // Calculate right vector
            float3 right = math.normalize(math.cross(up, dir));
            
            // Calculate UV coordinate (U is across road 0..1, V is along road)
            // Or use existing logic: V is textureT * Scale.
            float textureT = (float)i / totalSegments;

            samples.Add(new RoadSample
            {
                position = pos,
                right = right,
                up = up,
                vCoord = textureT * m_TextureScale
            });
        }

        // Optimize Geometry
        if (m_Optimize && samples.Count > 2)
        {
           List<RoadSample> optimized = new List<RoadSample>();
           optimized.Add(samples[0]);
           
           int lastIndex = 0;
           for (int i = 1; i < samples.Count - 1; i++)
           {
               Vector3 pPrev = samples[lastIndex].position;
               Vector3 pCurr = samples[i].position;
               Vector3 pNext = samples[i+1].position;

               Vector3 dir1 = (pCurr - pPrev).normalized;
               Vector3 dir2 = (pNext - pCurr).normalized;

               float angleDir = Vector3.Angle(dir1, dir2);
               float angleUp = Vector3.Angle(samples[lastIndex].up, samples[i].up);

               if (angleDir < m_OptimizeAngleThreshold && angleUp < m_OptimizeAngleThreshold)
               {
                   // Skip point
                   continue;
               }

               optimized.Add(samples[i]);
               lastIndex = i;
           }
           
           optimized.Add(samples[samples.Count - 1]);
           samples = optimized;
        }

        int vertexCount = samples.Count * 2;
        int indexCount = (samples.Count - 1) * 6;

        m_Positions.Capacity = Mathf.Max(m_Positions.Capacity, vertexCount);
        m_Normals.Capacity = Mathf.Max(m_Normals.Capacity, vertexCount);
        m_UVs.Capacity = Mathf.Max(m_UVs.Capacity, vertexCount);
        m_Indices.Capacity = Mathf.Max(m_Indices.Capacity, indexCount);

        float halfWidth = m_Width * 0.5f;

        for (int i = 0; i < samples.Count; i++)
        {
            RoadSample s = samples[i];
            
            // Add Vertices
            m_Positions.Add(s.position - (s.right * halfWidth));
            m_Positions.Add(s.position + (s.right * halfWidth));

            // Add Normals
            m_Normals.Add(s.up);
            m_Normals.Add(s.up);

            // Add UVs
            m_UVs.Add(new Vector2(0f, s.vCoord));
            m_UVs.Add(new Vector2(1f, s.vCoord));
        }

        // Generate Triangles
        for (int i = 0; i < samples.Count - 1; i++)
        {
            int baseIndex = i * 2;

            // Triangle 1
            m_Indices.Add(baseIndex);
            m_Indices.Add(baseIndex + 2);
            m_Indices.Add(baseIndex + 1);

            // Triangle 2
            m_Indices.Add(baseIndex + 1);
            m_Indices.Add(baseIndex + 2);
            m_Indices.Add(baseIndex + 3);
        }

        if (m_GenerateWalls)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                RoadSample s = samples[i];
                Vector3 pos = s.position;
                Vector3 up = s.up;
                Vector3 right = s.right;

                // Left wall
                Vector3 leftInnerBottom = pos - (right * (halfWidth + m_WallOffset));
                Vector3 leftInnerTop = leftInnerBottom + up * m_WallHeight;
                Vector3 leftOuterTop = leftInnerTop - right * m_WallThickness;
                Vector3 leftOuterBottom = leftInnerBottom - right * m_WallThickness;

                // Right wall
                Vector3 rightInnerBottom = pos + (right * (halfWidth + m_WallOffset));
                Vector3 rightInnerTop = rightInnerBottom + up * m_WallHeight;
                Vector3 rightOuterTop = rightInnerTop + right * m_WallThickness;
                Vector3 rightOuterBottom = rightInnerBottom + right * m_WallThickness;

                int baseIdx = m_Positions.Count;
                m_Positions.Add(leftInnerBottom);
                m_Positions.Add(leftInnerTop);
                m_Positions.Add(leftOuterTop);
                m_Positions.Add(leftOuterBottom);

                m_Positions.Add(rightOuterBottom);
                m_Positions.Add(rightOuterTop);
                m_Positions.Add(rightInnerTop);
                m_Positions.Add(rightInnerBottom);

                for (int n = 0; n < 8; n++) m_Normals.Add(up);

                float u = s.vCoord;
                m_UVs.Add(new Vector2(u, 0)); m_UVs.Add(new Vector2(u, 1)); m_UVs.Add(new Vector2(u, 1)); m_UVs.Add(new Vector2(u, 0));
                m_UVs.Add(new Vector2(u, 0)); m_UVs.Add(new Vector2(u, 1)); m_UVs.Add(new Vector2(u, 1)); m_UVs.Add(new Vector2(u, 0));

                if (i < samples.Count - 1)
                {
                    int currentBaseLeft = baseIdx;
                    int nextBaseLeft = baseIdx + 8;
                    
                    int currentBaseRight = baseIdx + 4;
                    int nextBaseRight = baseIdx + 12;

                    for (int j = 0; j < 4; j++)
                    {
                        int current = j;
                        int next = (j + 1) % 4;
                        
                        m_WallIndices.Add(currentBaseLeft + current);
                        m_WallIndices.Add(currentBaseLeft + next);
                        m_WallIndices.Add(nextBaseLeft + next);
                        
                        m_WallIndices.Add(currentBaseLeft + current);
                        m_WallIndices.Add(nextBaseLeft + next);
                        m_WallIndices.Add(nextBaseLeft + current);
                    }

                    for (int j = 0; j < 4; j++)
                    {
                        int current = j;
                        int next = (j + 1) % 4;
                        
                        m_WallIndices.Add(currentBaseRight + current);
                        m_WallIndices.Add(currentBaseRight + next);
                        m_WallIndices.Add(nextBaseRight + next);
                        
                        m_WallIndices.Add(currentBaseRight + current);
                        m_WallIndices.Add(nextBaseRight + next);
                        m_WallIndices.Add(nextBaseRight + current);
                    }
                }
            }
        }

        m_Mesh.SetVertices(m_Positions);
        m_Mesh.SetNormals(m_Normals);
        m_Mesh.SetUVs(0, m_UVs);

        m_Mesh.subMeshCount = m_GenerateWalls ? 2 : 1;
        m_Mesh.SetIndices(m_Indices, MeshTopology.Triangles, 0);
        if (m_GenerateWalls) m_Mesh.SetIndices(m_WallIndices, MeshTopology.Triangles, 1);

        m_Mesh.RecalculateNormals();
        m_Mesh.RecalculateBounds();

        MeshCollider meshCollider = GetComponent<MeshCollider>();
        if (meshCollider != null)
        {
            meshCollider.sharedMesh = m_Mesh;
        }
    }

    private struct RoadSample
    {
        public Vector3 position;
        public Vector3 right;
        public Vector3 up;
        public float vCoord;
    }
}
