using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine; // Use Cinemachine if on older versions

public class CameraSwitcher : MonoBehaviour
{
    public CinemachineCamera followCam;
    public CinemachineCamera freeFlyCam;

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame)
        {
            // Switch to FreeFly
            followCam.Priority = 10;
            freeFlyCam.Priority = 11;
        }
        if (Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame)
        {
            // Switch back to Follow
            followCam.Priority = 11;
            freeFlyCam.Priority = 10;
        }
    }
}