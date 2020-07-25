namespace KubeOps.Operator
{
    public sealed class OperatorSettings
    {
        public string Name { get; set; } = string.Empty;

        public string? ContainerImagePath { get; set; }
    }
}
