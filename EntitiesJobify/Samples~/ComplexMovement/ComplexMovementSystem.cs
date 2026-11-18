using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace EntitiesJobify.Sample.ComplexMovement
{
    public struct MovementVelocity : IComponentData
    {
        public float3 Value;
    }

    public struct MovementSettings : IComponentData
    {
        public bool CanMove;
    }

    public struct MovingTag : IComponentData { }
    public struct PausedTag : IComponentData { }

    [DisableAutoCreation]
    public partial struct ComplexMovementSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var deltaTime = SystemAPI.Time.DeltaTime;
            const float maxStep = 1f;
            var maxStepSquared = maxStep * maxStep;

            foreach (var (transform, velocity, settings) in
                     SystemAPI.Query<RefRW<LocalTransform>, RefRO<MovementVelocity>, RefRO<MovementSettings>>()
                         .WithAll<MovingTag>().WithNone<PausedTag>())
            {
                if (!settings.ValueRO.CanMove)
                    continue;

                var step = velocity.ValueRO.Value * deltaTime;
                if (math.lengthsq(step) > maxStepSquared)
                    step = math.normalizesafe(step) * maxStep;

                transform.ValueRW.Position += step;
            }
        }
    }
}
