﻿using System.Threading.Tasks;
using KubeOps.Operator;

namespace KubeOps.Build
{
    internal static class Program
    {
        public static Task Main(string[] args) => new KubernetesOperator().Run(args);
    }
}
