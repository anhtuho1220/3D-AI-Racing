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
    public bool generateWalls = true;
    public float wallThickness = 0.5f;
    public float wallHeight = 2f;
    public float wallOffset = -2f;
    public Material wallMaterial;

    [Header("Game Elements")]
    public GameObject checkpointPrefab;
    public int checkpointCount = 10;
    public float checkpointForwardOffset = 0f;
    public float checkpointYOffset = 1f;
    public float checkpointYRotation = 90f;
    public GameObject carPrefab;
    public GameObject playerCar;
    public bool spawnPlayerCar = true;
    public int carCount = 1;
    public float carSpacing = 8f;
    public float carYOffset = 0.5f;
    public float carRotationY = 0f;

    private SplineContainer splineContainer;
    private SplineRoadGenerator roadGenerator;
    private SplineCheckpointGenerator checkpointGenerator;

    [HideInInspector] public GameObject checkpointsContainer;
    [HideInInspector] public GameObject carObj;

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
            spline.Add(new BezierKnot(p, 0, 0, quaternion.identity), TangentMode.Continuous);
        }

        spline.Closed = true;

        // Second Pass: Calculate smooth Bezier tangents manually
        int count = spline.Count;
        for (int i = 0; i < count; i++)
        {
            var knot = spline[i];
            float3 currentPos = knot.Position;
            float3 prevPos = spline[(i - 1 + count) % count].Position;
            float3 nextPos = spline[(i + 1) % count].Position;

            // Catmull-Rom style tangent direction
            float3 tangentDir = math.normalize(nextPos - prevPos);
            
            // Scale handles based on distance to neighbor points independently
            // This prevents overshooting handles when points are unevenly spaced
            float distToPrev = math.distance(currentPos, prevPos);
            float distToNext = math.distance(currentPos, nextPos);

            // A standard multiplier is usually between 0.3f and 0.33f
            knot.TangentIn = -tangentDir * (distToPrev * 0.33f);
            knot.TangentOut = tangentDir * (distToNext * 0.33f);

            spline[i] = knot;
        }

        SetupRoadLoft();
        PlaceCar();
        SetupCheckpoints();
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
        
        roadGenerator.GenerateWalls = generateWalls;
        roadGenerator.WallThickness = wallThickness;
        roadGenerator.WallHeight = wallHeight;
        roadGenerator.WallOffset = wallOffset;
        roadGenerator.WallMaterial = wallMaterial;
        
        roadGenerator.Rebuild();
    }


    void SetupCheckpoints()
    {
        if (!TryGetComponent(out checkpointGenerator))
            checkpointGenerator = gameObject.AddComponent<SplineCheckpointGenerator>();

        checkpointGenerator.Container = splineContainer;
        checkpointGenerator.CheckpointPrefab = checkpointPrefab;
        checkpointGenerator.CheckpointCount = checkpointCount;
        checkpointGenerator.CheckpointForwardOffset = checkpointForwardOffset;
        checkpointGenerator.CheckpointYOffset = checkpointYOffset;
        checkpointGenerator.CheckpointYRotation = checkpointYRotation;
        checkpointGenerator.CarObj = carObj;
        
        checkpointGenerator.Rebuild();
    }

    void PlaceCar()
    {
        Spline spline = splineContainer.Spline;
        float splineLength = spline.GetLength();

        // 1. Find or create the container
        Transform container = transform.Find("CarsContainer");
        if (container == null)
        {
            GameObject containerObj = new GameObject("CarsContainer");
            containerObj.transform.parent = transform;
            containerObj.transform.localPosition = Vector3.zero;
            container = containerObj.transform;
        }

        // Clean up existing cars safely
        for (int i = container.childCount - 1; i >= 0; i--)
        {
            GameObject child = container.GetChild(i).gameObject;
            if (playerCar != null && child == playerCar)
            {
                child.transform.parent = transform; // Save from destruction
                continue;
            }
            DestroyImmediate(child);
        }

        // Safely clean up old single car if it exists in the scene and is a child of the track
        if (carObj != null && carObj.name != "CarsContainer")
        {
            if (carObj.scene.IsValid() && carObj.transform.IsChildOf(transform))
            {
                DestroyImmediate(carObj);
            }
        }

        carObj = container.gameObject;

        if (carPrefab == null)
        {
            Debug.LogWarning("RandomTrackGenerator: 'Car Prefab' is not assigned in the Inspector! Spawning blank placeholder cubes. Please drag your Car prefab into the 'Car Prefab' slot under Game Elements.");
        }

        if (playerCar != null)
        {
            playerCar.SetActive(spawnPlayerCar);
        }

        // 3. Spawn cars
        for (int i = 0; i < carCount; i++)
        {
            // Staggered grid spacing
            float dist = i * (carSpacing * 0.5f);
            float t = 0f;
            if (splineLength > 0f)
            {
                // Place backwards from the start line
                t = (splineLength - dist) / splineLength % 1f;
                if (t < 0) t += 1f;
            }

            float3 posFunc, tangentFunc, upFunc;
            SplineUtility.Evaluate(spline, t, out posFunc, out tangentFunc, out upFunc);

            // Offset cars to create a staggered 2-lane layout if more than 1 car
            float laneOffset = 0f;
            if (carCount > 1) 
            {
                laneOffset = (i % 2 == 0) ? -trackWidth * 0.2f : trackWidth * 0.2f;
            }

            float3 rightFunc = math.normalize(math.cross(upFunc, tangentFunc));

            Vector3 carPos = (Vector3)posFunc + (Vector3)rightFunc * laneOffset + (Vector3)upFunc * carYOffset;
            Quaternion carRot = Quaternion.LookRotation(tangentFunc, upFunc) * Quaternion.Euler(0, carRotationY, 0);

            GameObject newCar;
            bool isPlayer = (i == 0 && playerCar != null && spawnPlayerCar);

            if (isPlayer)
            {
                newCar = playerCar;
                newCar.transform.position = carPos;
                newCar.transform.rotation = carRot;
            }
            else
            {
                if (carPrefab != null)
                {
                    newCar = Instantiate(carPrefab, carPos, carRot);
                }
                else
                {
                    newCar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    newCar.transform.position = carPos;
                    newCar.transform.rotation = carRot;
                }
                newCar.name = $"Car_{i}";
            }
            
            newCar.transform.parent = carObj.transform;
        }
    }
}

