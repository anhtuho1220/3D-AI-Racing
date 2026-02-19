using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

[ExecuteInEditMode]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class WallGenerator : MonoBehaviour
{
    [SerializeField]
    private SplineContainer m_SplineContainer;

    [SerializeField]
    private Material m_Material;
    
    [SerializeField]
    private float m_TrackWidth = 10f;

    [SerializeField]
    private float m_WallOffset = 0f;

    [SerializeField]
    private float m_WallThickness = 0.5f;

    [SerializeField]
    private float m_WallHeight = 1f;

    [SerializeField]
    private int m_WallResolution = 10;
    
    [SerializeField]
    private bool m_EnableColliders = true;
    
    [SerializeField]
    private bool m_IsLeft = true;

    private Mesh m_Mesh;
    private bool m_RebuildRequested = false;

    // Properties for programmatic access
    public SplineContainer Container
    {
        get => m_SplineContainer;
        set { if (m_SplineContainer != value) { Unsubscribe(); m_SplineContainer = value; Subscribe(); Dirty(); } }
    }
    
    public Material WallMaterial
    {
        get => m_Material;
        set { if (m_Material != value) { m_Material = value; GetComponent<MeshRenderer>().sharedMaterial = m_Material; } }
    }
    
    public float TrackWidth { get => m_TrackWidth; set { if (m_TrackWidth != value) { m_TrackWidth = value; Dirty(); } } }
    public float WallOffset { get => m_WallOffset; set { if (m_WallOffset != value) { m_WallOffset = value; Dirty(); } } }
    public float WallThickness { get => m_WallThickness; set { if (m_WallThickness != value) { m_WallThickness = value; Dirty(); } } }
    public float WallHeight { get => m_WallHeight; set { if (m_WallHeight != value) { m_WallHeight = value; Dirty(); } } }
    public int WallResolution { get => m_WallResolution; set { if (m_WallResolution != value) { m_WallResolution = value; Dirty(); } } }
    public bool EnableColliders { get => m_EnableColliders; set { if (m_EnableColliders != value) { m_EnableColliders = value; Dirty(); } } }
    public bool IsLeft { get => m_IsLeft; set { if (m_IsLeft != value) { m_IsLeft = value; Dirty(); } } }

    private void OnEnable()
    {
        if (m_SplineContainer == null)
            m_SplineContainer = GetComponentInParent<SplineContainer>();
            
        Subscribe();
        Dirty();
    }

    private void OnDisable()
    {
        Unsubscribe();
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
                    Dirty();
                    break;
                }
            }
        }
    }

    private void OnSplineContainerAdded(SplineContainer container, int index)
    {
        if (container == m_SplineContainer) Dirty();
    }

    private void OnSplineContainerRemoved(SplineContainer container, int index)
    {
        if (container == m_SplineContainer) Dirty();
    }

    private void OnSplineContainerReordered(SplineContainer container, int index1, int index2)
    {
        if (container == m_SplineContainer) Dirty();
    }
    
    public void Dirty()
    {
        m_RebuildRequested = true;
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

        if (m_SplineContainer == null) return;
        Spline spline = m_SplineContainer.Spline;
        if (spline == null) return;

        if (m_Mesh == null)
        {
            m_Mesh = new Mesh();
            m_Mesh.name = "WallMesh";
            GetComponent<MeshFilter>().sharedMesh = m_Mesh;
        }
        
        MeshRenderer mr = GetComponent<MeshRenderer>();
        if (mr.sharedMaterial != m_Material)
        {
             mr.sharedMaterial = m_Material;
        }

        // Logic from original GenerateMeshWall
        
        int segmentsPerKnot = m_WallResolution; 
        if (segmentsPerKnot < 1) segmentsPerKnot = 1;
        
        int pointCount = spline.Count; // Assuming 1 knot per point based on original logic
        // Original logic: "Use spline count instead of pointCount (which was a member of RandomTrackGenerator)"
        // "We can approximate pointCount by checking knots"
        
        // Wait, RandomTrackGenerator passes pointCount. But here we just want to follow the spline resolution.
        // Let's use Spline length / resolution logic OR stick to Knot * Resolution.
        // The original code used `pointCount * segmentsPerKnot`. `pointCount` was `spline.Count`.
        // So Spline.Count * Resolution is fine.
        
        int totalSegments = pointCount * segmentsPerKnot;

        // However, if the spline is very long, maybe we want resolution based on length?
        // RandomTrackGenerator logic was: pointCount is the number of knots.
        // Let's stick to the previous implementation logic regarding segment count to preserve behavior.

        Vector3[] vertices = new Vector3[(totalSegments + 1) * 4];
        int[] triangles = new int[totalSegments * 24];
        Vector2[] uvs = new Vector2[vertices.Length];
        
        for (int i = 0; i <= totalSegments; i++)
        {
            float t = (float)i / totalSegments;
            if (i == totalSegments) t = 0f; // Wrap for position

            float3 posFunc, tangentFunc, upFunc;
            SplineUtility.Evaluate(spline, t, out posFunc, out tangentFunc, out upFunc);
            
            float3 tangent = math.normalize(tangentFunc);
            float3 up = new float3(0, 1, 0); // Force world up
            float3 right = math.normalize(math.cross(up, tangent));

            float offsetInner = (m_TrackWidth / 2f) + m_WallOffset;
            float offsetOuter = offsetInner + m_WallThickness;
            
            float3 posInner, posOuter;
            
            if (m_IsLeft)
            {
                posInner = posFunc - right * offsetInner;
                posOuter = posFunc - right * offsetOuter;
            }
            else
            {
                posInner = posFunc + right * offsetInner;
                posOuter = posFunc + right * offsetOuter;
            }
            
            Vector3 vInnerBottom = posInner;
            Vector3 vInnerTop = posInner + (float3)up * m_WallHeight;
            Vector3 vOuterTop = posOuter + (float3)up * m_WallHeight;
            Vector3 vOuterBottom = posOuter;

            int baseIdx = i * 4;
            
            // UVs
            float u = (float)i / totalSegments * 10f; // Arbitrary tiling factor from original

            if (m_IsLeft)
            {
                vertices[baseIdx + 0] = vInnerBottom;
                vertices[baseIdx + 1] = vInnerTop;
                vertices[baseIdx + 2] = vOuterTop;
                vertices[baseIdx + 3] = vOuterBottom;
                
                uvs[baseIdx + 0] = new Vector2(u, 0);
                uvs[baseIdx + 1] = new Vector2(u, 1);
                uvs[baseIdx + 2] = new Vector2(u, 1);
                uvs[baseIdx + 3] = new Vector2(u, 0);
            }
            else
            {
                vertices[baseIdx + 0] = vOuterBottom;
                vertices[baseIdx + 1] = vOuterTop;
                vertices[baseIdx + 2] = vInnerTop;
                vertices[baseIdx + 3] = vInnerBottom;
                
                uvs[baseIdx + 0] = new Vector2(u, 0);
                uvs[baseIdx + 1] = new Vector2(u, 1);
                uvs[baseIdx + 2] = new Vector2(u, 1);
                uvs[baseIdx + 3] = new Vector2(u, 0);
            }

            if (i < totalSegments)
            {
                int currentBase = i * 4;
                int nextBase = (i + 1) * 4;
                int tIdx = i * 24;

                for (int j = 0; j < 4; j++)
                {
                    int current = j;
                    int next = (j + 1) % 4;
                    
                    triangles[tIdx++] = currentBase + current;
                    triangles[tIdx++] = currentBase + next;
                    triangles[tIdx++] = nextBase + next;
                    
                    triangles[tIdx++] = currentBase + current;
                    triangles[tIdx++] = nextBase + next;
                    triangles[tIdx++] = nextBase + current;
                }
            }
        }

        m_Mesh.Clear();
        m_Mesh.vertices = vertices;
        m_Mesh.triangles = triangles;
        m_Mesh.uv = uvs;
        m_Mesh.RecalculateNormals();
        
        GetComponent<MeshFilter>().sharedMesh = m_Mesh;
        
        if (m_EnableColliders)
        {
            MeshCollider mc = GetComponent<MeshCollider>();
            if (mc == null) mc = gameObject.AddComponent<MeshCollider>();
            mc.sharedMesh = m_Mesh;
        }
        else
        {
             MeshCollider mc = GetComponent<MeshCollider>();
             if (mc != null) DestroyImmediate(mc);
        }
    }
}
