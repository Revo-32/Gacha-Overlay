namespace LSOverlay.Backend.Runtime;

// A reversible runtime cutover, not deletion of stored data or credentials.
internal sealed record BackendFeaturePolicy(bool CoreOnly);
