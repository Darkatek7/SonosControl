namespace SonosControl.Web.Services;

public sealed class EditorNavigationRequests
{
    private bool _newSceneRequested;

    public void RequestNewScene() => _newSceneRequested = true;

    public bool TakeNewSceneRequest()
    {
        var requested = _newSceneRequested;
        _newSceneRequested = false;
        return requested;
    }
}
