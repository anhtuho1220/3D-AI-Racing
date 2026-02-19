using UnityEngine;
using UnityEngine.Splines; 
using Unity.Mathematics;

[RequireComponent(typeof(SplineContainer))]
public class RandomTrackGenerator : MonoBehaviour
{
    [Header("Track Shape")]
    public int pointCount = 15;
    public float minPointDistance = 10f;
    public float radius = 50f;
    public float variation = 15f;
    public float minNoiseFreq = 1.0f;
    public float maxNoiseFreq = 10.0f;
    
    [Header("Road Settings")]
    public float trackWidth = 10f;
    public Material roadMaterial; // Material for the road
    public float roadResolution = 1.0f;
    public float textureScale = 64f;

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
    public float checkpointForwardOffset = 0f;
    public float checkpointYOffset = 1f;
    public float checkpointYRotation = 90f;
    public GameObject carPrefab;
    public float carYOffset = 0.5f;
    public float carRotationY = 0f;

    private SplineContainer splineContainer;
    private SplineRoadGenerator roadGenerator;

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
        float scaleX = UnityEngine.Random.Range(0.5f, 2f);
        float scaleZ = UnityEngine.Random.Range(0.5f, 2f);

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

        // Filter points to prevent overlapping segments
        var filteredPoints = new System.Collections.Generic.List<float3>();
        if (points.Count > 0)
        {
            filteredPoints.Add(points[0]);
            for (int i = 1; i < points.Count; i++)
            {
                if (math.distance(points[i], filteredPoints[filteredPoints.Count - 1]) >= minPointDistance)
                {
                    filteredPoints.Add(points[i]);
                }
            }
            
            // Check closure (last point vs first point)
            if (filteredPoints.Count > 2 && math.distance(filteredPoints[filteredPoints.Count - 1], filteredPoints[0]) < minPointDistance)
            {
                filteredPoints.RemoveAt(filteredPoints.Count - 1);
            }

            points = filteredPoints;
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

        SetupRoadLoft();
        GenerateWalls();
        PlaceCar();
        GenerateCheckpoints();
    }

    void SetupRoadLoft()
    {




        if (!TryGetComponent(out roadGenerator))
            roadGenerator = gameObject.AddComponent<SplineRoadGenerator>();

        roadGenerator.Container = splineContainer;
        roadGenerator.Width = trackWidth;
        roadGenerator.RoadMaterial = roadMaterial;
        roadGenerator.Resolution = roadResolution;
        roadGenerator.TextureScale = textureScale;
        
        roadGenerator.Rebuild();
    }

    void GenerateWalls()
    {
        // Update Left Wall
        if (leftWallObj == null)
        {
            leftWallObj = new GameObject("LeftWall");
            leftWallObj.transform.parent = transform;
            leftWallObj.transform.localPosition = Vector3.zero;
            leftWallObj.transform.localRotation = Quaternion.identity;
        }

        WallGenerator leftWg = leftWallObj.GetComponent<WallGenerator>();
        if (leftWg == null) leftWg = leftWallObj.AddComponent<WallGenerator>();

        leftWg.Container = splineContainer;
        leftWg.WallMaterial = leftWallMat != null ? leftWallMat : new Material(Shader.Find("Standard")) { color = new Color(1f, 0.92f, 0.016f, 0.3f) };
        leftWg.TrackWidth = trackWidth;
        leftWg.WallOffset = wallOffset;
        leftWg.WallThickness = wallThickness;
        leftWg.WallHeight = wallHeight;
        leftWg.WallResolution = wallResolution;
        leftWg.EnableColliders = enableWallColliders;
        leftWg.IsLeft = true;
        leftWg.Rebuild();

        // Update Right Wall
        if (rightWallObj == null)
        {
            rightWallObj = new GameObject("RightWall");
            rightWallObj.transform.parent = transform;
            rightWallObj.transform.localPosition = Vector3.zero;
            rightWallObj.transform.localRotation = Quaternion.identity;
        }

        WallGenerator rightWg = rightWallObj.GetComponent<WallGenerator>();
        if (rightWg == null) rightWg = rightWallObj.AddComponent<WallGenerator>();

        rightWg.Container = splineContainer;
        rightWg.WallMaterial = rightWallMat != null ? rightWallMat : new Material(Shader.Find("Standard")) { color = new Color(1f, 0.64f, 0f, 0.3f) };
        rightWg.TrackWidth = trackWidth;
        rightWg.WallOffset = wallOffset;
        rightWg.WallThickness = wallThickness;
        rightWg.WallHeight = wallHeight;
        rightWg.WallResolution = wallResolution;
        rightWg.EnableColliders = enableWallColliders;
        rightWg.IsLeft = false;
        rightWg.Rebuild();
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
        float splineLength = spline.GetLength();
        
        for (int i = 0; i < checkpointCount; i++)
        {
            float t = (float)i / checkpointCount;
            // Add offset based on spline length
            if (splineLength > 0)
            {
                t = (t + (checkpointForwardOffset / splineLength)) % 1f;
                if (t < 0) t += 1f; // Handle negative offset if needed
            }
            
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

