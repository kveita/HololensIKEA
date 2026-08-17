using Windows.UI.Input.Spatial;

namespace HololensIKEA.Common
{
    /// <summary>
    /// Tracks spatial interaction events: press (air-tap start), per-frame update while held,
    /// and release (air-tap end).  All three are exposed as poll-style methods safe to call
    /// from the render-thread Update loop.
    /// </summary>
    public class SpatialInputHandler
    {
        private SpatialInteractionManager interactionManager;

        // SourcePressed — consumed on first read (one-shot).
        private SpatialInteractionSourceState _pressedState;

        // SourceUpdated — replaced every frame while held; read-only (not consumed).
        private SpatialInteractionSourceState _updatedState;

        // SourceReleased — consumed on first read (one-shot).
        private bool _released;

        public SpatialInputHandler()
        {
            interactionManager = SpatialInteractionManager.GetForCurrentView();
            interactionManager.SourcePressed  += OnSourcePressed;
            interactionManager.SourceUpdated  += OnSourceUpdated;
            interactionManager.SourceReleased += OnSourceReleased;
        }

        /// <summary>Returns the SourcePressed state and clears it (one-shot).</summary>
        public SpatialInteractionSourceState CheckForInput()
        {
            var s = _pressedState;
            _pressedState = null;
            return s;
        }

        /// <summary>
        /// Returns the most recent SourceUpdated state while the interaction is held.
        /// Not consumed — returns the same value until the next SourceUpdated event.
        /// Returns null when no interaction is active.
        /// </summary>
        public SpatialInteractionSourceState CheckForUpdate() => _updatedState;

        /// <summary>Returns true once when SourceReleased fires, then false until the next release.</summary>
        public bool CheckForRelease()
        {
            var r = _released;
            _released = false;
            return r;
        }

        private void OnSourcePressed(SpatialInteractionManager sender, SpatialInteractionSourceEventArgs args)
        {
            _pressedState = args.State;
            _updatedState = args.State;   // seed update so first CheckForUpdate is non-null
        }

        private void OnSourceUpdated(SpatialInteractionManager sender, SpatialInteractionSourceEventArgs args)
        {
            _updatedState = args.State;
        }

        private void OnSourceReleased(SpatialInteractionManager sender, SpatialInteractionSourceEventArgs args)
        {
            _released     = true;
            _updatedState = null;
        }
    }
}

