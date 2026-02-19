using UnityEngine;

public class CheckpointSingle : MonoBehaviour
{
    public GameObject carObj;
    private TrackCheckpoints trackCheckpoints;

    private void OnTriggerEnter(Collider other)
    {
        if (carObj != null && (other.transform == carObj.transform || other.transform.IsChildOf(carObj.transform)))
        {
            if (trackCheckpoints != null)
                trackCheckpoints.CheckpointTriggered(this);
            else
                Debug.LogError("CheckpointSingle: trackCheckpoints reference not set!");
        }
    }

    public void SetTrackCheckpoints(TrackCheckpoints trackCheckpoints)
    {
        this.trackCheckpoints = trackCheckpoints;
    }
}
