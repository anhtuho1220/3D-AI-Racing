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

    [Header("Game Elements")]
    public GameObject checkpointPrefab;
    public int checkpointCount = 10;
    public GameObject carPrefab;
    public float carYOffset = 0.5f;

    private SplineContainer splineContainer;
    private SplineExtrude extrudeComponent;

    // Wall References
    private GameObject leftWallObj;
    private GameObject rightWallObj;
    private GameObject checkpointsContainer;
    private GameObject carObj;

    [ContextMenu("Generate Full Track")]
    public void Generate()
    {
        splineContainer = GetComponent<SplineContainer>();
        var spline = splineContainer.Spline;
        spline.Clear();

        // F1-style randomness: Use Perlin noise sampled in a circle to create organic, seamless shapes.
        // Also apply random aspect ratio to create L/R/Oval shapes instead of perfect circles.
        
        float seed = UnityEngine.Random.Range(0f, 100f);
        float noiseFreq = 2.0f; // How "wobbly" the track is (higher = more twists)
        
        // Random Aspect Ratio (Stretch X or Z) to break circular symmetry
        float scaleX = UnityEngine.Random.Range(0.8f, 1.8f);
        float scaleZ = UnityEngine.Random.Range(0.8f, 1.8f);

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

            float3 position = new float3(x, 0, z);

            // Add knot with default tangents first
            spline.Add(new BezierKnot(position, 0, 0, quaternion.identity), TangentMode.Mirrored);
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
            
            // Handle length logic (0.35f * dist)
            float dist = math.distance(prevPos, nextPos);
            float handleLen = dist * 0.35f;

            knot.TangentOut = tangentDir * handleLen;
            knot.TangentIn = -tangentDir * handleLen;

            spline[i] = knot;
        }

        SetupExtruder();
        GenerateWalls();
        GenerateCheckpoints();
        PlaceCar();
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
        // Remove old components if they exist (cleanup)
        if (leftWallObj != null) DestroyImmediate(leftWallObj);
        if (rightWallObj != null) DestroyImmediate(rightWallObj);

        GenerateMeshWall("LeftWall", ref leftWallObj, new Color(1f, 0.92f, 0.016f, 0.3f), true);
        GenerateMeshWall("RightWall", ref rightWallObj, new Color(1f, 0.64f, 0f, 0.3f), false);
    }

    void GenerateCheckpoints()
    {
        if (checkpointsContainer != null) DestroyImmediate(checkpointsContainer);
        checkpointsContainer = new GameObject("Checkpoints");
        checkpointsContainer.transform.parent = transform;
        checkpointsContainer.transform.localPosition = Vector3.zero;
        checkpointsContainer.transform.localRotation = Quaternion.identity;

        if (checkpointPrefab == null) return;

        Spline spline = splineContainer.Spline;
        
        for (int i = 0; i < checkpointCount; i++)
        {
            float t = (float)i / checkpointCount;
            
            float3 posFunc, tangentFunc, upFunc;
            SplineUtility.Evaluate(spline, t, out posFunc, out tangentFunc, out upFunc);

            GameObject cp = Instantiate(checkpointPrefab, (Vector3)posFunc, Quaternion.LookRotation(tangentFunc, upFunc));
            cp.transform.parent = checkpointsContainer.transform;
            cp.name = $"Checkpoint_{i}";
        }
    }

    void PlaceCar()
    {
        if (carObj != null) DestroyImmediate(carObj);
        
        Spline spline = splineContainer.Spline;
        float3 posFunc, tangentFunc, upFunc;
        SplineUtility.Evaluate(spline, 0f, out posFunc, out tangentFunc, out upFunc);

        Vector3 carPos = (Vector3)posFunc + (Vector3)upFunc * carYOffset;
        Quaternion carRot = Quaternion.LookRotation(tangentFunc, upFunc);

        if (carPrefab != null)
        {
            carObj = Instantiate(carPrefab, carPos, carRot);
            carObj.name = "PlayerCar";
        }
        else
        {
            // Placeholder cube
            carObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            carObj.name = "PlayerCar_Placeholder";
            carObj.transform.position = carPos;
            carObj.transform.rotation = carRot;
        }
    }

    void GenerateMeshWall(string name, ref GameObject wallObj, Color color, bool isLeft)
    {
        // Create Object
        wallObj = new GameObject(name);
        wallObj.transform.parent = transform;
        wallObj.transform.localPosition = Vector3.zero;
        wallObj.transform.localRotation = Quaternion.identity;

        MeshFilter mf = wallObj.AddComponent<MeshFilter>();
        MeshRenderer mr = wallObj.AddComponent<MeshRenderer>();
        MeshCollider mc = wallObj.AddComponent<MeshCollider>();

        // Material Setup
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Standard");
        Material mat = new Material(shader);
        mat.name = name + "_Mat";
        mat.color = color;
        if (shader.name == "Standard")
        {
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;
        }
        mr.sharedMaterial = mat;

        // Mesh Generation
        Spline spline = splineContainer.Spline;
        int segmentsPerKnot = wallResolution; 
        if (segmentsPerKnot < 1) segmentsPerKnot = 1;
        
        int totalSegments = pointCount * segmentsPerKnot;
        
        Vector3[] vertices = new Vector3[(totalSegments + 1) * 2];
        int[] triangles = new int[totalSegments * 6];
        Vector2[] uvs = new Vector2[vertices.Length];

        // Ensure spline is closed for calculations
        float length = spline.GetLength();
        
        for (int i = 0; i <= totalSegments; i++)
        {
            float t = (float)i / totalSegments;
            // Handle wrap around perfectly
            if (i == totalSegments) t = 0f; 

            // Evaluate Spline
            float3 posFunc, tangentFunc, upFunc;
            SplineUtility.Evaluate(spline, t, out posFunc, out tangentFunc, out upFunc);
            
            float3 tangent = math.normalize(tangentFunc);
            float3 up = new float3(0, 1, 0); // Force world up for walls
            float3 right = math.normalize(math.cross(up, tangent));

            // Offset
            float offsetDist = (trackWidth / 2f) + wallOffset;
            
            float3 basePos;
            if (isLeft)
                basePos = posFunc - right * offsetDist;
            else
                basePos = posFunc + right * offsetDist;

            // Vertices
            int vIndex = i * 2;
            vertices[vIndex] = basePos; // Bottom
            vertices[vIndex + 1] = basePos + (float3)up * wallHeight; // Top

            // UVs
            float v = (float)i / totalSegments * 10f; // Tile along length
            uvs[vIndex] = new Vector2(v, 0);
            uvs[vIndex + 1] = new Vector2(v, 1);

            // Triangles (skip last point as it connects to 0, but since we duplicate vertices for closing... 
            // actually standard ring buffer mesh generation usually duplicates the start vertices at end OR indexes back to 0.
            // Let's index back to 0 for the last segment to be seamless? 
            // Better: just generate `totalSegments` quads.
            
            if (i < totalSegments)
            {
                int tIndex = i * 6;
                int currentBottom = vIndex;
                int currentTop = vIndex + 1;
                int nextBottom = (vIndex + 2);
                int nextTop = (vIndex + 3);

                if (isLeft) // Outward facing? 
                {
                    // CCW winding
                    triangles[tIndex] = currentBottom;
                    triangles[tIndex + 1] = nextBottom;
                    triangles[tIndex + 2] = currentTop;

                    triangles[tIndex + 3] = currentTop;
                    triangles[tIndex + 4] = nextBottom;
                    triangles[tIndex + 5] = nextTop;
                }
                else
                {
                    // Clockwise or flip? Right wall inner face should be visible? 
                    // Usually we want double sided or inward facing for track walls.
                    // Let's assume standard CCW winding for "outward" normal relative to wall center.
                    // If we want the face pointing towards the road:
                    // Right wall is at +Right. Road is at -Right.
                    // We want normals pointing -Right.
                    // We want Opposite winding. 0-1-2.
                    
                    triangles[tIndex] = currentBottom;
                    triangles[tIndex + 1] = currentTop; // Swapped
                    triangles[tIndex + 2] = nextBottom; // Swapped

                    triangles[tIndex + 3] = currentTop;
                    triangles[tIndex + 4] = nextTop;
                    triangles[tIndex + 5] = nextBottom;
                }
            }
        }

        Mesh mesh = new Mesh();
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uvs;
        mesh.RecalculateNormals();
        
        mf.mesh = mesh;
        mc.sharedMesh = mesh;
    }
}