using UnityEngine;
using UnityEngine.Splines; 
using Unity.Mathematics;
using System.Collections.Generic;

[RequireComponent(typeof(SplineContainer))]
public class RandomTrackGenerator : MonoBehaviour
{
    [Header("Track Shape")]
    public int pointCount = 20;
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
    public int maxGenerationAttempts = 20;
    [Tooltip("Minimum turn angle dot product. Corners sharper than this are softened. -0.5 = 120°")]
    public float minCornerDot = -0.5f;
    [Tooltip("How aggressively midpoints are displaced inwards (0 = convex only, 1 = very concave)")]
    [Range(0f, 1f)]
    public float displacementStrength = 0.6f;
    [Tooltip("Number of Chaikin smoothing passes (more = smoother)")]
    [Range(1, 5)]
    public int smoothingPasses = 3;

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

            // Check that the road surface (with width) doesn't overlap itself
            if (!HasSelfIntersection(points) && !HasWidthOverlap(points, trackWidth * 0.85f))
            {
                valid = true;
                break;
            }

            Debug.LogWarning($"Track generation attempt {attempt + 1}: self-intersection or width-overlap detected. Retrying...");
        }

        if (!valid)
        {
            Debug.LogError($"Could not generate a valid track after {maxGenerationAttempts} attempts. " +
                           "Try increasing radius, decreasing variation/displacementStrength, or adjusting minPointDistance.");
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

    // ─────────────────────────────────────────────────────────
    // F1-STYLE TRACK GENERATION (Convex Hull + Displacement)
    // ─────────────────────────────────────────────────────────

    private List<float3> GenerateTrackPoints()
    {
        // ── Step 1: Scatter random seed points ──
        var scatterPoints = ScatterRandomPoints(pointCount, radius);

        // ── Step 2: Compute convex hull → guaranteed non-intersecting base ──
        var hull = ConvexHull(scatterPoints);
        if (hull.Count < 3) return hull;

        // ── Step 3: Midpoint displacement → add concavities (chicanes, hairpins) ──
        var displaced = MidpointDisplace(hull, displacementStrength);

        // ── Step 4: Chaikin subdivision smoothing → organic curves ──
        var smooth = displaced;
        for (int i = 0; i < smoothingPasses; i++)
            smooth = ChaikinSmooth(smooth);

        // ── Step 5: Enforce minimum spacing ──
        var filtered = EnforceMinDistance(smooth, minPointDistance);

        // ── Step 6: Randomize direction ──
        bool isClockwise = UnityEngine.Random.value > 0.5f;
        if (isClockwise)
            filtered.Reverse();

        return filtered;
    }

    /// <summary>
    /// Scatter random points inside an ellipse with random aspect ratio.
    /// </summary>
    private List<float2> ScatterRandomPoints(int count, float rad)
    {
        float scaleX = UnityEngine.Random.Range(0.6f, 1.6f);
        float scaleZ = UnityEngine.Random.Range(0.6f, 1.6f);

        var pts = new List<float2>();
        for (int i = 0; i < count; i++)
        {
            // Use rejection sampling inside the ellipse
            float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float dist = Mathf.Sqrt(UnityEngine.Random.Range(0f, 1f)) * rad;
            float x = Mathf.Cos(angle) * dist * scaleX;
            float z = Mathf.Sin(angle) * dist * scaleZ;
            pts.Add(new float2(x, z));
        }
        return pts;
    }

    /// <summary>
    /// Compute the 2D convex hull (Andrew's monotone chain).
    /// Returns float3 points (y=0) in counter-clockwise order.
    /// </summary>
    private List<float3> ConvexHull(List<float2> points)
    {
        points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));

        int n = points.Count;
        if (n < 3) 
        {
            var res = new List<float3>();
            foreach (var p in points) res.Add(new float3(p.x, 0, p.y));
            return res;
        }

        var hull = new List<float2>();

        // Lower hull
        for (int i = 0; i < n; i++)
        {
            while (hull.Count >= 2 && Cross2D(hull[hull.Count - 2], hull[hull.Count - 1], points[i]) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(points[i]);
        }

        // Upper hull
        int lowerCount = hull.Count + 1;
        for (int i = n - 2; i >= 0; i--)
        {
            while (hull.Count >= lowerCount && Cross2D(hull[hull.Count - 2], hull[hull.Count - 1], points[i]) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(points[i]);
        }

        hull.RemoveAt(hull.Count - 1); // Remove last point (duplicate of first)

        var result = new List<float3>();
        foreach (var p in hull)
            result.Add(new float3(p.x, 0, p.y));
        return result;
    }

    /// <summary>
    /// Displace midpoints of hull edges to create concave sections.
    /// Displacement is perpendicular to the edge, toward or away from the centroid.
    /// </summary>
    private List<float3> MidpointDisplace(List<float3> hull, float strength)
    {
        // Calculate centroid
        float3 centroid = float3.zero;
        foreach (var p in hull) centroid += p;
        centroid /= hull.Count;

        var result = new List<float3>();
        int n = hull.Count;

        for (int i = 0; i < n; i++)
        {
            float3 a = hull[i];
            float3 b = hull[(i + 1) % n];

            result.Add(a);

            // Calculate midpoint
            float3 mid = (a + b) * 0.5f;

            // Direction from midpoint toward centroid
            float3 toCenter = math.normalize(centroid - mid);

            // Edge length determines max displacement magnitude
            float edgeLen = math.distance(a, b);

            // Random displacement: sometimes push inward, sometimes leave roughly on the hull
            // Bias toward inward displacement to create interesting concavities
            float displaceMag = UnityEngine.Random.Range(-0.1f, 1.0f) * strength * edgeLen * 0.5f;

            // Add perpendicular jitter for more natural look
            float3 edgeDir = math.normalize(b - a);
            float3 perp = new float3(-edgeDir.z, 0, edgeDir.x);
            float perpJitter = UnityEngine.Random.Range(-0.15f, 0.15f) * edgeLen;

            float3 displaced = mid + toCenter * displaceMag + perp * perpJitter;
            result.Add(displaced);
        }

        return result;
    }

    /// <summary>
    /// Chaikin's corner-cutting subdivision for smooth curves.
    /// Each iteration doubles the point count and rounds corners.
    /// </summary>
    private List<float3> ChaikinSmooth(List<float3> pts)
    {
        var result = new List<float3>();
        int n = pts.Count;

        for (int i = 0; i < n; i++)
        {
            float3 p0 = pts[i];
            float3 p1 = pts[(i + 1) % n];

            // Cut at 25% and 75% of each segment
            result.Add(math.lerp(p0, p1, 0.25f));
            result.Add(math.lerp(p0, p1, 0.75f));
        }

        return result;
    }

    /// <summary>
    /// Remove points that are too close together.
    /// </summary>
    private List<float3> EnforceMinDistance(List<float3> pts, float minDist)
    {
        if (pts.Count == 0) return pts;

        var filtered = new List<float3> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            if (math.distance(pts[i], filtered[filtered.Count - 1]) >= minDist)
                filtered.Add(pts[i]);
        }

        // Check closure: last vs first
        if (filtered.Count > 2 && math.distance(filtered[filtered.Count - 1], filtered[0]) < minDist)
            filtered.RemoveAt(filtered.Count - 1);

        return filtered;
    }

    // ─────────────────────────────────────────────────────────
    // START / CORNER REFINEMENT
    // ─────────────────────────────────────────────────────────

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

    // ─────────────────────────────────────────────────────────
    // SELF-INTERSECTION & WIDTH-OVERLAP DETECTION
    // ─────────────────────────────────────────────────────────

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

    /// <summary>
    /// Check that no two non-adjacent track segments come closer than 'minClearance'.
    /// This prevents the road surface from overlapping even when centerlines don't cross.
    /// </summary>
    private bool HasWidthOverlap(List<float3> pts, float minClearance)
    {
        int n = pts.Count;
        float sqClear = minClearance * minClearance;

        for (int i = 0; i < n; i++)
        {
            float2 a1 = new float2(pts[i].x, pts[i].z);
            float2 a2 = new float2(pts[(i + 1) % n].x, pts[(i + 1) % n].z);

            // Check against non-adjacent segments (skip self and neighbors)
            for (int j = i + 3; j < n; j++)
            {
                // Also skip if j+1 wraps to be adjacent to i
                if (i == 0 && j >= n - 2) continue;

                float2 b1 = new float2(pts[j].x, pts[j].z);
                float2 b2 = new float2(pts[(j + 1) % n].x, pts[(j + 1) % n].z);

                float dist = SegmentToSegmentDistanceSq(a1, a2, b1, b2);
                if (dist < sqClear) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Squared distance between two 2D line segments.
    /// </summary>
    private float SegmentToSegmentDistanceSq(float2 p1, float2 p2, float2 p3, float2 p4)
    {
        // Check all 4 point-to-segment distances and take the minimum
        float d1 = PointToSegmentDistanceSq(p1, p3, p4);
        float d2 = PointToSegmentDistanceSq(p2, p3, p4);
        float d3 = PointToSegmentDistanceSq(p3, p1, p2);
        float d4 = PointToSegmentDistanceSq(p4, p1, p2);

        float min = math.min(math.min(d1, d2), math.min(d3, d4));

        // If segments actually intersect, distance is 0
        if (SegmentsIntersect(p1, p2, p3, p4)) return 0f;

        return min;
    }

    /// <summary>
    /// Squared distance from point p to line segment (a, b).
    /// </summary>
    private float PointToSegmentDistanceSq(float2 p, float2 a, float2 b)
    {
        float2 ab = b - a;
        float2 ap = p - a;
        float t = math.dot(ap, ab) / math.dot(ab, ab);
        t = math.clamp(t, 0f, 1f);
        float2 closest = a + ab * t;
        float2 diff = p - closest;
        return math.dot(diff, diff);
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

    // ─────────────────────────────────────────────────────────
    // ROAD / CHECKPOINTS / CARS (unchanged)
    // ─────────────────────────────────────────────────────────

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

    [ContextMenu("Flip Track Direction")]
    public void FlipTrackDirection()
    {
        splineContainer = GetComponent<SplineContainer>();
        if (splineContainer == null || splineContainer.Spline == null) return;

        var spline = splineContainer.Spline;
        int count = spline.Count;
        if (count < 2)
        {
            Debug.LogWarning("FlipTrackDirection: Not enough knots to flip.");
            return;
        }

        // Collect all knots
        var knots = new BezierKnot[count];
        for (int i = 0; i < count; i++)
            knots[i] = spline[i];

        // Rebuild the spline in reversed order with swapped tangents.
        // When reversing a cubic Bézier spline:
        //   - The knot order is reversed
        //   - Each knot's TangentIn becomes the old TangentOut (negated) and vice-versa
        spline.Clear();
        for (int i = count - 1; i >= 0; i--)
        {
            var k = knots[i];
            var flipped = new BezierKnot(
                k.Position,
                k.TangentOut,   // old out → new in
                k.TangentIn,    // old in  → new out
                k.Rotation
            );
            spline.Add(flipped, TangentMode.Broken);
        }

        spline.Closed = true;

        // Rebuild visuals and game objects
        SetupRoadLoft();
        PlaceCar();
        SetupCheckpoints();

        Debug.Log("Track direction flipped successfully.");
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
