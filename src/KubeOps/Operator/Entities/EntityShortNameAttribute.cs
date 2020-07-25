﻿using System;

namespace KubeOps.Operator.Entities
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class EntityShortNameAttribute : Attribute
    {
        public EntityShortNameAttribute(string shortName)
        {
            if (shortName.ToLowerInvariant() != shortName)
                throw new ArgumentOutOfRangeException(nameof(shortName), "The shortnames need to be all lowercase");
            ShortName = shortName;
        }

        public string ShortName { get; }
    }
}
