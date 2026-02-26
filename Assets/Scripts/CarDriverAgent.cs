using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using UnityEngine.InputSystem;

public class CarDriverAgent : Agent
{
    private TrackCheckpoints trackCheckpoints;
    private Vector3 startPosition;
    private Quaternion startRotation;

    private PrometeoAIController carController;
    private Rigidbody carRigidbody;
    private RayPerceptionSensorComponent3D raySensorComponent;

    public override void Initialize()
    {
        carController = GetComponent<PrometeoAIController>();
        carRigidbody = GetComponent<Rigidbody>();
        raySensorComponent = GetComponent<RayPerceptionSensorComponent3D>();
        trackCheckpoints = FindFirstObjectByType<TrackCheckpoints>();
        startPosition = transform.position;
        startRotation = transform.rotation;
    }

    void Start()
    {
        if (trackCheckpoints != null)
        {
            trackCheckpoints.OnCarCorrectCheckpoint += OnCorrectCheckpoint;
            trackCheckpoints.OnCarWrongCheckpoint += OnWrongCheckpoint;
        }
    }

    private void OnCorrectCheckpoint(object sender, TrackCheckpoints.CarCheckpointEventArgs e)
    {
        if (e.carTransform == transform) {
            AddReward(1f);
        }
    }

    private void OnWrongCheckpoint(object sender, TrackCheckpoints.CarCheckpointEventArgs e)
    {
        if (e.carTransform == transform) {
            AddReward(-1f);
        }
    }

    public override void OnEpisodeBegin()
    {
        transform.position = startPosition;
        transform.rotation = startRotation;
        
        if (trackCheckpoints != null) trackCheckpoints.ResetCar(transform);
        carController.ResetCar();
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        var nextCheckpoint = trackCheckpoints.GetNextCheckpoint(transform);
        if (nextCheckpoint != null)
        {
            Vector3 checkpointForward = nextCheckpoint.transform.forward;
            float directionDot = Vector3.Dot(transform.forward, checkpointForward);
            sensor.AddObservation(directionDot);
        }
        else
        {
            sensor.AddObservation(0f);
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        if (actions.ContinuousActions.Length >= 2 && actions.DiscreteActions.Length >= 1)
        {
            float throttle = actions.ContinuousActions[0];
            float steering = actions.ContinuousActions[1];
            bool brake = actions.DiscreteActions[0] == 1;
            
            carController.SetInputs(steering, throttle, brake);

            if (carRigidbody != null)
            {
                float forwardSpeed = Vector3.Dot(carRigidbody.linearVelocity, transform.forward);
                if (forwardSpeed > 0.1f)
                {
                    AddReward(0.05f);
                }
            }

            if (raySensorComponent != null && raySensorComponent.RaySensor != null)
            {
                var rayOutput = raySensorComponent.RaySensor.RayPerceptionOutput;
                var rayInput = raySensorComponent.GetRayPerceptionInput();

                if (rayOutput.RayOutputs != null && rayOutput.RayOutputs.Length > 0 && rayInput.Angles.Count > 0)
                {
                    int leftRayIndex = -1;
                    int rightRayIndex = -1;
                    float maxAngle = float.MinValue;
                    float minAngle = float.MaxValue;

                    for (int i = 0; i < rayInput.Angles.Count; i++)
                    {
                        if (rayInput.Angles[i] > maxAngle)
                        {
                            maxAngle = rayInput.Angles[i];
                            leftRayIndex = i;
                        }
                        if (rayInput.Angles[i] < minAngle)
                        {
                            minAngle = rayInput.Angles[i];
                            rightRayIndex = i;
                        }
                    }

                    if (leftRayIndex >= 0 && rightRayIndex >= 0)
                    {
                        var leftRay = rayOutput.RayOutputs[leftRayIndex];
                        var rightRay = rayOutput.RayOutputs[rightRayIndex];

                        if (leftRay.HitTaggedObject && rightRay.HitTaggedObject)
                        {
                            if (rayInput.DetectableTags[leftRay.HitTagIndex] == "Left walls" && rayInput.DetectableTags[rightRay.HitTagIndex] == "Right walls")
                            {
                                AddReward(0.01f);
                            }
                        }

                    }
                }
            }
        }
        else
        {
            Debug.LogError("ML-Agents Behavior Parameters are not configured properly. Continuous Actions must be 2, and Discrete Branches must be 1.");
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        float throttle = 0f;
        float steering = 0f;
        int brake = 0;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed) {
                throttle = 1f;
            }
            else if (Keyboard.current.sKey.isPressed) {
                throttle = -1f;
            }

            if (Keyboard.current.aKey.isPressed) {
                steering = -1f;
            }
            else if (Keyboard.current.dKey.isPressed) {
                steering = 1f;
            }

            if (Keyboard.current.spaceKey.isPressed) {
                brake = 1;
            }
        }
        
        if (actionsOut.ContinuousActions.Length >= 2)
        {
            var continuousActionsOut = actionsOut.ContinuousActions;
            continuousActionsOut[0] = throttle;
            continuousActionsOut[1] = steering;
        }

        if (actionsOut.DiscreteActions.Length >= 1)
        {
            var discreteActionsOut = actionsOut.DiscreteActions;
            discreteActionsOut[0] = brake;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (collision.gameObject.TryGetComponent<Wall>(out var wall))
        {
            AddReward(-0.5f);
        }
    }

    private void OnCollisionStay(Collision collision)
    {
        if (collision.gameObject.TryGetComponent<Wall>(out var wall))
        {
            AddReward(-0.1f);
        }
    }

}
