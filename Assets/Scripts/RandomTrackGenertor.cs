using UnityEngine;
using UnityEngine.Splines; // This contains SplineExtrude
using Unity.Mathematics;

[RequireComponent(typeof(SplineContainer))]
public class RandomTrackGenerator : MonoBehaviour
{
    [Header("Track Shape")]
    public int pointCount = 15;
    public float radius = 50f;
    public float variation = 15f;
    public float minNoiseFreq = 1.0f;
    public float maxNoiseFreq = 4.0f;
    
    [Header("Road Settings")]
    public float trackWidth = 10f;
    // Banking removed

    [Header("Wall Settings")]
    public float wallThickness = 0.5f;
    public float wallHeight = 2f; // Extrude radius
    public float wallOffset = -2f; // Offset from track edge
    public int wallResolution = 10; // Segments per knot
    public Material leftWallMat;
    public Material rightWallMat;
    public bool enableWallColliders = true;

    [Header("Game Elements")]
    public GameObject checkpointPrefab;
    public int checkpointCount = 10;
    public float checkpointYOffset = 1f;
    public float checkpointYRotation = 90f;
    public GameObject carPrefab;
    public float carYOffset = 0.5f;
    public float carRotationY = 0f;

    private SplineContainer splineContainer;
    private SplineExtrude extrudeComponent;

    // Wall References
    public GameObject leftWallObj;
    public GameObject rightWallObj;
    public GameObject checkpointsContainer;
    public GameObject carObj;

    [ContextMenu("Generate Full Track")]
    public void Generate()
    {
        splineContainer = GetComponent<SplineContainer>();
        var spline = splineContainer.Spline;
        spline.Clear();

        // F1-style randomness: Use Perlin noise sampled in a circle to create organic, seamless shapes.
        // Also apply random aspect ratio to create L/R/Oval shapes instead of perfect circles.
        
        float seed = UnityEngine.Random.Range(0f, 100f);
        float noiseFreq = UnityEngine.Random.Range(minNoiseFreq, maxNoiseFreq);
        
        // Random Aspect Ratio (Stretch X or Z) to break circular symmetry
        float scaleX = UnityEngine.Random.Range(0.8f, 1.8f);
        float scaleZ = UnityEngine.Random.Range(0.8f, 1.8f);

        bool isClockwise = UnityEngine.Random.value > 0.5f;

        System.Collections.Generic.List<float3> points = new System.Collections.Generic.List<float3>();

        for (int i = 0; i < pointCount; i++)
        {
            float t = (float)i / pointCount;
            float angle = t * Mathf.PI * 2;

            // Sample 2D Perlin noise in a circle to ensure the start and end match perfectly (Seamless loop)
            float noiseX = math.cos(angle) * noiseFreq;
            float noiseY = math.sin(angle) * noiseFreq;
            float noiseVal = Mathf.PerlinNoise(seed + noiseX, seed + noiseY); // 0..1

            // Map noise 0..1 to -variation..variation
            float currentVar = Mathf.Lerp(-variation, variation, noiseVal);

            // Clamp radius 
            float effectiveRadius = Mathf.Max(radius + currentVar, trackWidth * 2.5f);

            // Apply nonuniform scaling
            float x = math.cos(angle) * effectiveRadius * scaleX;
            float z = math.sin(angle) * effectiveRadius * scaleZ;

            points.Add(new float3(x, 0, z));
        }

        if (isClockwise)
        {
            points.Reverse();
        }

        foreach (var p in points)
        {
            spline.Add(new BezierKnot(p, 0, 0, quaternion.identity), TangentMode.Mirrored);
        }

        spline.Closed = true;

        // Second Pass: Calculate smooth Bezier tangents manually
        int count = spline.Count;
        for (int i = 0; i < count; i++)
        {
            var knot = spline[i];
            float3 prevPos = spline[(i - 1 + count) % count].Position;
            float3 nextPos = spline[(i + 1) % count].Position;

            // Catmull-Rom style tangent direction
            float3 tangentDir = math.normalize(nextPos - prevPos);
            
            // Handle length logic (0.3f * dist)
            float dist = math.distance(prevPos, nextPos);
            float handleLen = dist * 0.3f;

            knot.TangentOut = tangentDir * handleLen;
            knot.TangentIn = -tangentDir * handleLen;

            spline[i] = knot;
        }

        SetupExtruder();
        GenerateWalls();
        PlaceCar();
        GenerateCheckpoints();
    }

    void SetupExtruder()
    {
        if (!TryGetComponent(out extrudeComponent))
            extrudeComponent = gameObject.AddComponent<SplineExtrude>();

        extrudeComponent.Container = splineContainer;
        extrudeComponent.Radius = trackWidth / 2f; 
        extrudeComponent.Sides = 2; // Flat road
        extrudeComponent.Rebuild();
    }

    void GenerateWalls()
    {
        // Reuse existing objects
        leftWallObj = WallGenerator.GenerateMeshWall(
            "LeftWall", 
            transform, 
            splineContainer.Spline, 
            leftWallMat, 
            new Color(1f, 0.92f, 0.016f, 0.3f), 
            trackWidth, 
            wallOffset, 
            wallThickness, 
            wallHeight, 
            wallResolution, 
            enableWallColliders, 
            true,
            leftWallObj
        );

        rightWallObj = WallGenerator.GenerateMeshWall(
            "RightWall", 
            transform, 
            splineContainer.Spline, 
            rightWallMat, 
            new Color(1f, 0.64f, 0f, 0.3f), 
            trackWidth, 
            wallOffset, 
            wallThickness, 
            wallHeight, 
            wallResolution, 
            enableWallColliders, 
            false,
            rightWallObj
        );
    }

    void GenerateCheckpoints()
    {
        if (checkpointsContainer == null) 
        {
            checkpointsContainer = new GameObject("Checkpoints");
            checkpointsContainer.transform.parent = transform;
            checkpointsContainer.transform.localPosition = Vector3.zero;
            checkpointsContainer.transform.localRotation = Quaternion.identity;
        }
        else
        {
             // Clear existing checkpoints
             // Use a backward loop or while loop for immediate destruction
             while (checkpointsContainer.transform.childCount > 0)
             {
                 DestroyImmediate(checkpointsContainer.transform.GetChild(0).gameObject);
             }
        }

        if (checkpointPrefab == null) return;

        Spline spline = splineContainer.Spline;
        
        for (int i = 0; i < checkpointCount; i++)
        {
            float t = (float)i / checkpointCount;
            
            float3 posFunc, tangentFunc, upFunc;
            SplineUtility.Evaluate(spline, t, out posFunc, out tangentFunc, out upFunc);

            Vector3 position = (Vector3)posFunc + Vector3.up * checkpointYOffset;
            Quaternion rotation = Quaternion.LookRotation(tangentFunc, upFunc) * Quaternion.Euler(0, checkpointYRotation, 0);

            GameObject cp = Instantiate(checkpointPrefab, position, rotation);
            cp.transform.parent = checkpointsContainer.transform;
            cp.name = $"Checkpoint_{i}";

            CheckpointSingle checkScript = cp.GetComponentInChildren<CheckpointSingle>();
            if (checkScript != null)
            {
                checkScript.carObj = carObj;
            }
        }
    }

    void PlaceCar()
    {
        Spline spline = splineContainer.Spline;
        float3 posFunc, tangentFunc, upFunc;
        SplineUtility.Evaluate(spline, 0f, out posFunc, out tangentFunc, out upFunc);

        Vector3 carPos = (Vector3)posFunc + (Vector3)upFunc * carYOffset;
        Quaternion carRot = Quaternion.LookRotation(tangentFunc, upFunc) * Quaternion.Euler(0, carRotationY, 0);

        if (carObj != null)
        {
            carObj.transform.position = carPos;
            carObj.transform.rotation = carRot;
            // Ensure Unity knows it's the same object, but position changed
        }
        else
        {
            if (carPrefab != null)
            {
                carObj = Instantiate(carPrefab, carPos, carRot);
                carObj.name = "Cars";
            }
            else
            {
                // Placeholder cube
                carObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                carObj.name = "Car_Placeholder";
                carObj.transform.position = carPos;
                carObj.transform.rotation = carRot;
            }
        }
    }


}