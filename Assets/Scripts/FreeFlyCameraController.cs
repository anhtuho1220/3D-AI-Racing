using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

public class FreeFlyCamera : MonoBehaviour
{
    public CinemachineCamera followCam;
    public CinemachineCamera freeFlyCam;

    public float speed = 10f;
    public float mouseSensitivity = 0.5f;

    private float xRotation = 0f;
    private float yRotation = 0f;

    void Start()
    {
        Vector3 rot = transform.localRotation.eulerAngles;
        yRotation = rot.y;
        xRotation = rot.x;
        // Adjust xRotation if it's over 180 so clamping works correctly
        if (xRotation > 180f) xRotation -= 360f;
    }

    void Update()
    {
        // Don't register inputs if FreeFlyCam is inactive/lower priority
        if (freeFlyCam != null && followCam != null && freeFlyCam.Priority < followCam.Priority)
            return;

        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            float h = 0;
            if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) h += 1;
            if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) h -= 1;

            float v = 0;
            if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) v += 1;
            if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) v -= 1;
            
            // Move the camera relative to its own orientation
            Vector3 move = transform.right * h + transform.forward * v;
            transform.position += move * speed * Time.deltaTime;
            
            // Move up/down with Q/E
            if (keyboard.eKey.isPressed) transform.position += Vector3.up * speed * Time.deltaTime;
            if (keyboard.qKey.isPressed) transform.position += Vector3.down * speed * Time.deltaTime;
        }

        var mouse = Mouse.current;
        if (mouse != null && mouse.rightButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue();

            yRotation += delta.x * mouseSensitivity;
            xRotation -= delta.y * mouseSensitivity;

            // Clamp vertical rotation so you don't flip upside down
            xRotation = Mathf.Clamp(xRotation, -90f, 90f);

            transform.localRotation = Quaternion.Euler(xRotation, yRotation, 0f);
        }
    }
}
