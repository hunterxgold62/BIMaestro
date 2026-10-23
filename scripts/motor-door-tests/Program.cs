using System;
using System.Collections.Generic;
using BIMaestro.MepBooster;

static class Program
{
    private static readonly MotorVector X = new MotorVector(1, 0, 0), Y = new MotorVector(0, 1, 0), Z = new MotorVector(0, 0, 1);
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }
    private static List<MotorVector> Sweep(MotorVector start, MotorVector end, double width = .8, double length = 1.2,
        double height = 1, double yaw = 0)
    {
        var x = new MotorVector(Math.Cos(yaw), Math.Sin(yaw), 0);
        var y = new MotorVector(-Math.Sin(yaw), Math.Cos(yaw), 0);
        var vertices = new List<MotorVector>();
        foreach (var center in new[] { start, end })
            for (int i = 0; i < 8; i++)
                vertices.Add(center + x * ((i & 1) == 0 ? -width / 2 : width / 2)
                    + y * ((i & 2) == 0 ? -length / 2 : length / 2)
                    + Z * ((i & 4) == 0 ? -height / 2 : height / 2));
        return vertices;
    }
    private static void Main()
    {
        var door = new MotorDoorPortal("door", new MotorVector(0, 0, 0), X, Y, Z, 1, 2.1, .2);
        Check(door.AcceptsSweep(Sweep(new MotorVector(0, -2, 1), new MotorVector(0, 2, 1))), "A centered motor fits");
        Check(!door.AcceptsSweep(Sweep(new MotorVector(0, -2, 1), new MotorVector(0, 2, 1), 1.01)), "An oversized motor is refused");
        Check(!door.AcceptsSweep(Sweep(new MotorVector(.2, -2, 1), new MotorVector(.2, 2, 1))), "An offset motor clips the jamb");
        Check(!door.AcceptsSweep(Sweep(new MotorVector(0, -2, 1.8), new MotorVector(0, 2, 1.8))), "An elevated motor clips the lintel");
        Check(!door.AcceptsSweep(Sweep(new MotorVector(0, -2, .3), new MotorVector(0, 2, .3))), "A motor cannot extend below the sill");
        Check(door.AcceptsSweep(Sweep(new MotorVector(5, -2, 1), new MotorVector(5, 2, 1))), "A different doorway on the same plane is not constrained");
        Check(door.AcceptsSweep(Sweep(new MotorVector(0, -3, 1), new MotorVector(0, -2, 1), 2)), "An oversized object away from the portal can turn or move");
        Check(!door.AcceptsSweep(Sweep(new MotorVector(0, -2, 1), new MotorVector(0, 2, 1), .5, 1.4, 1, Math.PI / 2)), "Turning the long side across the opening is refused");
        Check(door.AcceptsSweep(Sweep(new MotorVector(0, -2, 1), new MotorVector(0, 2, 1), .5, 1.4)), "The same motor passes with its long side forward");

        // The endpoint bounding box spans x = -2.15 ... 2.15; only its
        // intersection with the wall thickness must fit through the aperture.
        var diagonal = Sweep(new MotorVector(-2, -4, 1), new MotorVector(2, 4, 1), .3, .3);
        Check(door.AcceptsSweep(diagonal), "A long diagonal translation is clipped at the wall, not enclosed in an AABB");
        Check(!door.AcceptsSweep(Sweep(new MotorVector(-2, -2, 1), new MotorVector(2, 2, 1), .8, .8)), "The corners of a diagonal crossing still collide");
        Check(!door.AcceptsSweep(Sweep(new MotorVector(-2, -.1, 1), new MotorVector(2, .1, 1), .3, .3)), "Moving sideways through the jamb along the wall is refused");

        // Equivalent linked-model door under a 90 degree rotation and offset.
        var linkedDoor = new MotorDoorPortal("linked", new MotorVector(10, 20, 3), Y, X * -1, Z, 1, 2.1, .2);
        var transformed = new List<MotorVector>();
        foreach (var p in diagonal) transformed.Add(new MotorVector(10 - p.Y, 20 + p.X, 3 + p.Z));
        Check(linkedDoor.AcceptsSweep(transformed), "Linked door coordinates preserve the aperture check");
        Check(door.AcceptsSweep(Sweep(new MotorVector(0, -2, 1), new MotorVector(0, 2, 1), 1)), "Exactly fitting nominal dimensions are accepted");
        Check(!door.AcceptsSweep(Sweep(new MotorVector(0, -2, 1), new MotorVector(0, 2, 1), 1.0001)), "A small real oversize is not rounded away");
        Console.WriteLine($"Motor door portal: {_checks} checks passed.");
    }
}
