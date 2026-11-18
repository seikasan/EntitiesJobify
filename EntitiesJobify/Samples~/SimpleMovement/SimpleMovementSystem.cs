using Unity.Entities;

namespace EntitiesJobify.Sample
{
    public struct Position : IComponentData
    {
        public float X;
    }

    public struct Velocity : IComponentData
    {
        public float X;
    }

    public partial struct SimpleMovementSystem : ISystem
    {
        public void OnCreate(ref SystemState state) { }
        public void OnDestroy(ref SystemState state) { }

        public void OnUpdate(ref SystemState state)
        {
            const float delta = 0.5f;
            foreach (var (velocity, position) in SystemAPI.Query<RefRO<Velocity>, RefRW<Position>>())
            {
                position.ValueRW.X += velocity.ValueRO.X * delta;
            }
        }
    }
}