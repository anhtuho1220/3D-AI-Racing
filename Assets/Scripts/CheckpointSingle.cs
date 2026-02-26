using UnityEngine;

public class CheckpointSingle : MonoBehaviour
{
    public GameObject carObj;
    private TrackCheckpoints trackCheckpoints;

    private void OnTriggerEnter(Collider other)
    {
        if (carObj != null && (other.transform == carObj.transform || other.transform.IsChildOf(carObj.transform)))
        {
            Transform carTransform = other.transform;
            // The actual car is a direct child of the carObj (CarsContainer).
            while (carTransform.parent != null && carTransform.parent != carObj.transform)
            {
                carTransform = carTransform.parent;
            }

            if (trackCheckpoints != null)
                trackCheckpoints.CheckpointTriggered(this, carTransform);
            else
                Debug.LogError("CheckpointSingle: trackCheckpoints reference not set!");
        }
    }

    public void SetTrackCheckpoints(TrackCheckpoints trackCheckpoints)
    {
        this.trackCheckpoints = trackCheckpoints;
    }
}
