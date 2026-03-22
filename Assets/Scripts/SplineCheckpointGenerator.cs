using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

[ExecuteInEditMode]
[RequireComponent(typeof(SplineContainer))]
public class SplineCheckpointGenerator : MonoBehaviour
{
    [SerializeField] private SplineContainer m_SplineContainer;
    [SerializeField] private GameObject m_CheckpointPrefab;
    [SerializeField] private int m_CheckpointCount = 10;
    [SerializeField] private float m_CheckpointForwardOffset = 0f;
    [SerializeField] private float m_CheckpointYOffset = 1f;
    [SerializeField] private float m_CheckpointYRotation = 90f;
    [SerializeField] private GameObject m_CarObj;

    private bool m_RebuildRequested = false;
    private GameObject m_CheckpointsContainer;

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

    public GameObject CheckpointPrefab
    {
        get => m_CheckpointPrefab;
        set
        {
            if (m_CheckpointPrefab != value)
            {
                m_CheckpointPrefab = value;
                m_RebuildRequested = true;
            }
        }
    }

    public int CheckpointCount
    {
        get => m_CheckpointCount;
        set
        {
            if (m_CheckpointCount != value)
            {
                m_CheckpointCount = value;
                m_RebuildRequested = true;
            }
        }
    }

    public float CheckpointForwardOffset
    {
        get => m_CheckpointForwardOffset;
        set
        {
            if (Math.Abs(m_CheckpointForwardOffset - value) > 0.001f)
            {
                m_CheckpointForwardOffset = value;
                m_RebuildRequested = true;
            }
        }
    }

    public float CheckpointYOffset
    {
        get => m_CheckpointYOffset;
        set
        {
            if (Math.Abs(m_CheckpointYOffset - value) > 0.001f)
            {
                m_CheckpointYOffset = value;
                m_RebuildRequested = true;
            }
        }
    }

    public float CheckpointYRotation
    {
        get => m_CheckpointYRotation;
        set
        {
            if (Math.Abs(m_CheckpointYRotation - value) > 0.001f)
            {
                m_CheckpointYRotation = value;
                m_RebuildRequested = true;
            }
        }
    }

    public GameObject CarObj
    {
        get => m_CarObj;
        set
        {
            if (m_CarObj != value)
            {
                m_CarObj = value;
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
        if (m_CheckpointPrefab == null) return;

        if (m_CheckpointsContainer == null)
        {
            Transform existing = transform.Find("Checkpoints");
            if (existing != null)
            {
                m_CheckpointsContainer = existing.gameObject;
            }
            else
            {
                m_CheckpointsContainer = new GameObject("Checkpoints");
                m_CheckpointsContainer.transform.parent = transform;
                m_CheckpointsContainer.transform.localPosition = Vector3.zero;
                m_CheckpointsContainer.transform.localRotation = Quaternion.identity;
            }
        }

        // Cleanup existing
        for (int i = m_CheckpointsContainer.transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = m_CheckpointsContainer.transform.GetChild(i).gameObject;
            if (Application.isPlaying)
            {
                child.transform.parent = null;
                Destroy(child);
            }
            else
            {
                DestroyImmediate(child);
            }
        }

        Spline spline = m_SplineContainer.Spline;
        float splineLength = spline.GetLength();
        
        for (int i = 0; i < m_CheckpointCount; i++)
        {
            float t = (float)i / m_CheckpointCount;
            if (splineLength > 0)
            {
                t = (t + (m_CheckpointForwardOffset / splineLength)) % 1f;
                if (t < 0) t += 1f;
            }
            
            float3 posFunc, tangentFunc, upFunc;
            SplineUtility.Evaluate(spline, t, out posFunc, out tangentFunc, out upFunc);

            Vector3 position = (Vector3)posFunc + Vector3.up * m_CheckpointYOffset;
            Quaternion rotation = Quaternion.LookRotation(tangentFunc, upFunc) * Quaternion.Euler(0, m_CheckpointYRotation, 0);

            GameObject cp = Instantiate(m_CheckpointPrefab, position, rotation);
            cp.transform.parent = m_CheckpointsContainer.transform;
            cp.name = $"Checkpoint_{i}";

            CheckpointSingle checkScript = cp.GetComponentInChildren<CheckpointSingle>();
            if (checkScript != null)
            {
                checkScript.carObj = m_CarObj;
            }
        }

        TrackCheckpoints trackCheckpoints = GetComponent<TrackCheckpoints>();
        if (trackCheckpoints != null)
        {
            trackCheckpoints.RefreshCheckpoints();
        }
    }
}
