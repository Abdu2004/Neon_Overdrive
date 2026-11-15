using Godot;
using ApexCombatRacing.Vehicles;

namespace ApexCombatRacing.Utils;

/// <summary>
/// Chase camera for arcade racers.
///
/// * Position is exponentially smoothed toward a rig offset in the car's local space.
/// * Look target is smoothed separately and leads the car's velocity, so the car
///   stays framed during hard drifts instead of sliding out of view.
/// * FOV widens with speed / nitro for a cheap sense of velocity (no motion blur,
///   which the Compatibility renderer does not support well anyway).
///
/// Runs in _PhysicsProcess so its smoothing is frame-rate independent and in sync
/// with the car's kinematic movement.
/// </summary>
[GlobalClass]
public partial class SmoothFollowCamera : Camera3D
{
    /// <summary>Vehicle or other node to chase. Assigned by the scene builder.</summary>
    public Node3D? Target { get; set; }

    [ExportGroup("Rig")]
    [Export] public Vector3 Offset { get; set; } = new(0.0f, 4.0f, 18.0f);
    [Export(PropertyHint.Range, "0.5,3,0.05")] public float LookHeight { get; set; } = 1.5f;
    /// <summary>How far ahead of the car we aim, per second of travel.</summary>
    [Export(PropertyHint.Range, "0,2,0.05")] public float LookAheadSeconds { get; set; } = 0.22f;

    [ExportGroup("Smoothing")]
    [Export(PropertyHint.Range, "1,30,0.5")] public float PositionSharpness { get; set; } = 9.0f;
    [Export(PropertyHint.Range, "1,30,0.5")] public float LookSharpness { get; set; } = 12.0f;
    [Export] public float MinHeight { get; set; } = 1.4f;

    [ExportGroup("Field of View")]
    [Export(PropertyHint.Range, "40,110,1")] public float BaseFov { get; set; } = 68.0f;
    [Export(PropertyHint.Range, "40,120,1")] public float MaxFov { get; set; } = 82.0f;
    [Export(PropertyHint.Range, "0.5,10,0.5")] public float FovSharpness { get; set; } = 4.0f;

    private Vector3 _lookTarget;

    public override void _Ready()
    {
        base._Ready();

        // Tight near plane: arcade speeds + a low camera clip the floor otherwise.
        Near = 0.15f;
        Far = 800.0f;
        Fov = BaseFov;

        SnapToTarget();
    }

    public override void _PhysicsProcess(double delta)
    {
        Follow((float)delta);
    }

    /// <summary>Instantly places the camera (used on spawn / teleport, avoids a swoop-in).</summary>
    public void SnapToTarget()
    {
        if (!IsInstanceValid(Target))
        {
            return;
        }

        GlobalPosition = ComputeDesiredPosition();
        _lookTarget = ComputeLookTarget();
        LookAt(_lookTarget, Vector3.Up);
        MakeCurrent();
    }

    private void Follow(float delta)
    {
        if (!IsInstanceValid(Target))
        {
            return;
        }

        float positionBlend = 1.0f - Mathf.Exp(-PositionSharpness * delta);
        Vector3 desired = ComputeDesiredPosition();
        GlobalPosition = GlobalPosition.Lerp(desired, positionBlend);

        // Never let the chase rig dip through the ground plane.
        Vector3 clamped = GlobalPosition;
        clamped.Y = Mathf.Max(clamped.Y, MinHeight);
        GlobalPosition = clamped;

        float lookBlend = 1.0f - Mathf.Exp(-LookSharpness * delta);
        _lookTarget = _lookTarget.Lerp(ComputeLookTarget(), lookBlend);

        // LookAt() errors if the target coincides with the camera position.
        if (GlobalPosition.DistanceSquaredTo(_lookTarget) > 0.001f)
        {
            LookAt(_lookTarget, Vector3.Up);
        }

        UpdateFov(delta);
    }

    private Vector3 ComputeDesiredPosition()
    {
        if (Target == null)
        {
            return GlobalPosition;
        }

        // Offset lives in the car's local space, so the camera swings behind it.
        return Target.GlobalTransform * Offset;
    }

    private Vector3 ComputeLookTarget()
    {
        if (Target == null)
        {
            return _lookTarget;
        }

        Vector3 look = Target.GlobalPosition + Vector3.Up * LookHeight;

        if (Target is BaseVehicle vehicle)
        {
            // Aim ahead of where the car is actually travelling, not where it points.
            look += vehicle.PlanarVelocity * LookAheadSeconds;
        }

        return look;
    }

    private void UpdateFov(float delta)
    {
        float speedRatio = 0.0f;
        bool nitro = false;

        if (Target is BaseVehicle vehicle)
        {
            speedRatio = Mathf.Clamp(vehicle.SpeedKph / Mathf.Max(vehicle.MaxSpeedKph, 1.0f), 0.0f, 1.25f);
            nitro = vehicle is CarController car && car.IsNitroActive;
        }

        float targetFov = Mathf.Lerp(BaseFov, MaxFov, Mathf.Min(speedRatio, 1.0f));
        if (nitro)
        {
            targetFov = Mathf.Max(targetFov, MaxFov);
        }

        Fov = Mathf.Lerp(Fov, targetFov, 1.0f - Mathf.Exp(-FovSharpness * delta));
    }
}
