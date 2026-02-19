using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

public static class WallGenerator
{
    public static GameObject GenerateMeshWall(
        string name, 
        Transform parent, 
        Spline spline, 
        Material assignedMat, 
        Color fallbackColor, 
        float trackWidth, 
        float wallOffset, 
        float wallThickness, 
        float wallHeight, 
        int wallResolution, 
        bool enableColliders, 
        bool isLeft,
        GameObject existingObj = null)
    {
        // Create or Reuse Object
        GameObject wallObj = existingObj;
        if (wallObj == null)
        {
            wallObj = new GameObject(name);
            wallObj.transform.parent = parent;
            wallObj.transform.localPosition = Vector3.zero;
            wallObj.transform.localRotation = Quaternion.identity;
        }
        else
        {
             // Ensure correct name and parent just in case
             wallObj.name = name;
             if (wallObj.transform.parent != parent)
             {
                 wallObj.transform.parent = parent;
                 wallObj.transform.localPosition = Vector3.zero;
                 wallObj.transform.localRotation = Quaternion.identity;
             }
        }

        MeshFilter mf = wallObj.GetComponent<MeshFilter>();
        if (mf == null) mf = wallObj.AddComponent<MeshFilter>();
        
        MeshRenderer mr = wallObj.GetComponent<MeshRenderer>();
        if (mr == null) mr = wallObj.AddComponent<MeshRenderer>();

        // Material Setup
        if (assignedMat != null)
        {
            mr.sharedMaterial = assignedMat;
        }
        else
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Standard");
            Material mat = new Material(shader);
            mat.name = name + "_Mat";
            mat.color = fallbackColor;
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
        }

        // Mesh Generation
        int segmentsPerKnot = wallResolution; 
        if (segmentsPerKnot < 1) segmentsPerKnot = 1;
        
        // Use spline count instead of pointCount (which was a member of RandomTrackGenerator)
        // We can approximate pointCount by checking knots, assuming 1 knot per point.
        // Actually, RandomTrackGenerator passed pointCount separately, but here we can just use the spline's knot count
        // OR we can pass segmentsPerKnot directly if we want tight control.
        // The original code used `pointCount * segmentsPerKnot`. `pointCount` essentially == `spline.Count` in the generator loop.
        int pointCount = spline.Count;
        int totalSegments = pointCount * segmentsPerKnot;
        
        // 4 vertices per segment (Tube profile: InnerBottom, InnerTop, OuterTop, OuterBottom)
        Vector3[] vertices = new Vector3[(totalSegments + 1) * 4];
        // 4 faces * 2 tris * 3 indices = 24 indices per segment
        int[] triangles = new int[totalSegments * 24];
        Vector2[] uvs = new Vector2[vertices.Length];

        // Ensure spline is closed for calculations? The original code did `spline.GetLength()`, but `t` was 0..1 based on loop count.
        
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

            // Offsets
            float offsetInner = (trackWidth / 2f) + wallOffset;
            float offsetOuter = offsetInner + wallThickness;
            
            // Calculate 4 corners
            
            float3 posInner, posOuter;
            
            if (isLeft)
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
            Vector3 vInnerTop = posInner + (float3)up * wallHeight;
            Vector3 vOuterTop = posOuter + (float3)up * wallHeight;
            Vector3 vOuterBottom = posOuter;

            // Assign to array in CCW order for the cross-section
            int baseIdx = i * 4;
            
            if (isLeft)
            {
                // CCW Loop: InnerBottom -> InnerTop -> OuterTop -> OuterBottom
                vertices[baseIdx + 0] = vInnerBottom;
                vertices[baseIdx + 1] = vInnerTop;
                vertices[baseIdx + 2] = vOuterTop;
                vertices[baseIdx + 3] = vOuterBottom;
                
                // UVs
                float u = (float)i / totalSegments * 10f;
                uvs[baseIdx + 0] = new Vector2(u, 0);
                uvs[baseIdx + 1] = new Vector2(u, 1);
                uvs[baseIdx + 2] = new Vector2(u, 1);
                uvs[baseIdx + 3] = new Vector2(u, 0);
            }
            else
            {
                // CCW Loop: OuterBottom -> OuterTop -> InnerTop -> InnerBottom
                vertices[baseIdx + 0] = vOuterBottom;
                vertices[baseIdx + 1] = vOuterTop;
                vertices[baseIdx + 2] = vInnerTop;
                vertices[baseIdx + 3] = vInnerBottom;
                
                // UVs
                float u = (float)i / totalSegments * 10f;
                uvs[baseIdx + 0] = new Vector2(u, 0);
                uvs[baseIdx + 1] = new Vector2(u, 1);
                uvs[baseIdx + 2] = new Vector2(u, 1);
                uvs[baseIdx + 3] = new Vector2(u, 0);
            }

            // Triangles
            if (i < totalSegments)
            {
                int currentBase = i * 4;
                int nextBase = (i + 1) * 4;
                int tIdx = i * 24;

                // 4 Faces
                for (int j = 0; j < 4; j++)
                {
                    int current = j;
                    int next = (j + 1) % 4;
                    
                    // Triangle 1
                    triangles[tIdx++] = currentBase + current;
                    triangles[tIdx++] = currentBase + next;
                    triangles[tIdx++] = nextBase + next;
                    
                    // Triangle 2
                    triangles[tIdx++] = currentBase + current;
                    triangles[tIdx++] = nextBase + next;
                    triangles[tIdx++] = nextBase + current;
                }
            }
        }

        Mesh mesh = mf.sharedMesh;
        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.name = name + "_Mesh";
        }
        else
        {
            mesh.Clear();
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.uv = uvs;
        mesh.RecalculateNormals();
        
        mf.sharedMesh = mesh;
        
        if (enableColliders)
        {
            MeshCollider mc = wallObj.GetComponent<MeshCollider>();
            if (mc == null) mc = wallObj.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
        }

        return wallObj;
    }
}
