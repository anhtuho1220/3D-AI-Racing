using UnityEngine;
using UnityEngine.Splines; 
using Unity.Mathematics;
using System.Collections.Generic;

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
    public Material roadMaterial;
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
    public bool disableCarCollision = false;

    [Header("Generation Settings")]
    [Tooltip("Maximum attempts to generate a non-self-intersecting track")]
    public int maxGenerationAttempts = 10;
    [Tooltip("Minimum turn angle dot product. Corners sharper than this are softened. -0.5 = 120°")]
    public float minCornerDot = -0.5f;

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

        List<float3> points = null;
        bool valid = false;

        for (int attempt = 0; attempt < maxGenerationAttempts; attempt++)
        {
            points = GenerateTrackPoints();

            if (points.Count < 3)
            {
                Debug.LogWarning($"Track generation attempt {attempt + 1}: only {points.Count} points survived filtering. Retrying...");
                continue;
            }

            if (!HasSelfIntersection(points))
            {
                valid = true;
                break;
            }

            Debug.LogWarning($"Track generation attempt {attempt + 1}: self-intersection detected. Retrying...");
        }

        if (!valid)
        {
            Debug.LogError($"Could not generate a valid track after {maxGenerationAttempts} attempts. " +
                           "Try increasing pointCount, decreasing variation, or adjusting minPointDistance.");
            if (points == null || points.Count < 3) return;
        }

        // Soften any dangerously sharp corners
        SoftenSharpCorners(points);

        // Straighten the start/finish area
        StraightenStartArea(points);

        foreach (var p in points)
        {
            spline.Add(new BezierKnot(p, 0, 0, quaternion.identity), TangentMode.Continuous);
        }

        spline.Closed = true;

        // Second Pass: Calculate smooth Bezier tangents manually
        ComputeSmoothTangents(spline);

        SetupRoadLoft();
        PlaceCar();
        SetupCheckpoints();
    }

    private List<float3> GenerateTrackPoints()
    {
        // F1-style randomness: Use Perlin noise sampled in a circle to create organic, seamless shapes.
        // Also apply random aspect ratio to create L/R/Oval shapes instead of perfect circles.

        float seed = UnityEngine.Random.Range(0f, 100f);
        float noiseFreq = UnityEngine.Random.Range(minNoiseFreq, maxNoiseFreq);
        
        // Random Aspect Ratio (Stretch X or Z) to break circular symmetry
        float scaleX = UnityEngine.Random.Range(0.5f, 2f);
        float scaleZ = UnityEngine.Random.Range(0.5f, 2f);

        bool isClockwise = UnityEngine.Random.value > 0.5f;

        var points = new List<float3>();

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
        var filteredPoints = new List<float3>();
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

        return points;
    }

    private void StraightenStartArea(List<float3> points)
    {
        if (points.Count < 3) return;

        float3 p0 = points[0];
        float3 p1 = points[1];
        float3 pLast = points[points.Count - 1];

        float3 dir = math.normalize(p1 - pLast);

        float dist1 = math.distance(p0, p1);
        float distLast = math.distance(p0, pLast);

        points[1] = p0 + dir * dist1;
        points[points.Count - 1] = p0 - dir * distLast;
    }

    private void SoftenSharpCorners(List<float3> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            float3 prev = points[(i - 1 + points.Count) % points.Count];
            float3 curr = points[i];
            float3 next = points[(i + 1) % points.Count];

            float3 dirIn = math.normalize(curr - prev);
            float3 dirOut = math.normalize(next - curr);
            float dot = math.dot(dirIn, dirOut);

            // Angle is too sharp — push the point outward to soften
            if (dot < minCornerDot)
            {
                float3 midDir = math.normalize(dirIn + dirOut);
                // If dirIn and dirOut are nearly opposite, midDir can be zero — use perpendicular instead
                if (math.lengthsq(midDir) < 0.001f)
                {
                    midDir = new float3(-dirIn.z, 0, dirIn.x);
                }
                points[i] = curr + midDir * trackWidth;
            }
        }
    }

    private void ComputeSmoothTangents(Spline spline)
    {
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
    }

    #region Self-Intersection Detection

    private bool HasSelfIntersection(List<float3> pts)
    {
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            float2 a1 = new float2(pts[i].x, pts[i].z);
            float2 a2 = new float2(pts[(i + 1) % n].x, pts[(i + 1) % n].z);

            // Skip adjacent segments (they share a point)
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue; // first and last share closure point

                float2 b1 = new float2(pts[j].x, pts[j].z);
                float2 b2 = new float2(pts[(j + 1) % n].x, pts[(j + 1) % n].z);

                if (SegmentsIntersect(a1, a2, b1, b2)) return true;
            }
        }
        return false;
    }

    private bool SegmentsIntersect(float2 p1, float2 p2, float2 p3, float2 p4)
    {
        float d1 = Cross2D(p3, p4, p1);
        float d2 = Cross2D(p3, p4, p2);
        float d3 = Cross2D(p1, p2, p3);
        float d4 = Cross2D(p1, p2, p4);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;

        return false;
    }

    private float Cross2D(float2 a, float2 b, float2 c)
    {
        return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
    }

    #endregion

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

    [ContextMenu("Re-Place Cars")]
    public void PlaceCar()
    {
        if (splineContainer == null) splineContainer = GetComponent<SplineContainer>();
        if (splineContainer == null || splineContainer.Spline == null) return;

        Spline spline = splineContainer.Spline;
        float splineLength = spline.GetLength();

        // Find or create the container
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
            SafeDestroy(child);
        }

        // Safely clean up old single car if it exists in the scene and is a child of the track
        if (carObj != null && carObj.name != "CarsContainer")
        {
            if (carObj.scene.IsValid() && carObj.transform.IsChildOf(transform))
            {
                SafeDestroy(carObj);
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

        // Spawn cars
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
            }
            else
            {
                if (carPrefab != null)
                {
                    newCar = Instantiate(carPrefab);
                }
                else
                {
                    newCar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                }
                newCar.name = $"Car_{i}";
            }
            
            newCar.transform.parent = carObj.transform;
            newCar.transform.localPosition = carPos;
            newCar.transform.localRotation = carRot;
        }

        SetupCarCollisionIgnoring();
    }

    private void SetupCarCollisionIgnoring()
    {
        var carCollidersList = new List<Collider[]>();
        for (int i = 0; i < carObj.transform.childCount; i++)
        {
            carCollidersList.Add(carObj.transform.GetChild(i).GetComponentsInChildren<Collider>(true));
        }

        for (int i = 0; i < carCollidersList.Count; i++)
        {
            for (int j = i + 1; j < carCollidersList.Count; j++)
            {
                foreach (Collider colA in carCollidersList[i])
                {
                    foreach (Collider colB in carCollidersList[j])
                    {
                        if (colA != null && colB != null)
                        {
                            Physics.IgnoreCollision(colA, colB, disableCarCollision);
                        }
                    }
                }
            }
        }
    }

    private void SafeDestroy(GameObject obj)
    {
        if (Application.isPlaying)
            Destroy(obj);
        else
            DestroyImmediate(obj);
    }
}
