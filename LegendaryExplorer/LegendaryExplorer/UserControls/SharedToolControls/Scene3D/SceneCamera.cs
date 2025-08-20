using LegendaryExplorerCore.Helpers;
using System;
using System.Numerics;

namespace LegendaryExplorer.UserControls.SharedToolControls.Scene3D
{
    public class SceneCamera
    {
        private Vector3 position = Vector3.Zero;
        private float pitch = 0;
        private float yaw = 0;
        // Ignore Roll for now. Who would ever roll their preview camera?
        public Vector3 Position
        {
            get => position;
            set
            {
                position = value;
                CalcViewMatrix();
            }
        }
        public float Pitch
        {
            get => pitch;
            set
            {
                pitch = value;
                CalcViewMatrix();
            }
        }
        public float Yaw
        {
            get => yaw;
            set
            {
                yaw = value;
                CalcViewMatrix();
            }
        }

        public float FocusDepth = 0; // Depth of rotation center for 3rd person mode.
        public float aspect = 1.0f;
        public float FOV = MathF.PI / 3; // 60 degrees.
        public float ZNear = 0.1f;
        public float ZFar = 400_000f;
        public bool FirstPerson = false;
        public Vector3 CameraUp => Vector3.Transform(Vector3.UnitY, Matrix4x4.CreateRotationX(Pitch) * Matrix4x4.CreateRotationY(Yaw)) * new Vector3(1, 1, -1);

        public Vector3 CameraLeft => Vector3.Transform(-Vector3.UnitX, Matrix4x4.CreateRotationY(-Yaw));

        public Vector3 CameraForward => Vector3.Transform(Vector3.UnitZ, Matrix4x4.CreateRotationX(-Pitch) * Matrix4x4.CreateRotationY(-Yaw));

        public SceneCamera()
        {
            CalcViewMatrix();
        }

        public SceneCamera(Vector3 Position)
        {
            this.Position = Position;
            CalcViewMatrix();
        }

        public void OrientTowards(Vector3 point)
        {
            Vector3 direction = (point - Position).Normal();
            Pitch = MathF.Asin(-direction.Y);
            Yaw = MathF.Atan2(direction.X, direction.Z);
        }

        private Matrix4x4 firstPersonViewMatrix;
        public Matrix4x4 ViewMatrix
        {
            get
            {
                if (FirstPerson)
                {
                    return firstPersonViewMatrix;
                }
                else
                {
                    return firstPersonViewMatrix * Matrix4x4.CreateTranslation(0, 0, FocusDepth);
                }
            }
        }

        private void CalcViewMatrix()
        {
            firstPersonViewMatrix = Matrix4x4.CreateTranslation(-Position) * Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateRotationX(Pitch);
        }

        public Matrix4x4 ProjectionMatrix
        {
            get
            {
                // This creates a left handed FoV matrix - code ported from SharpDX

                var matrix = new Matrix4x4();
                float yScale = (float)(1.0f / Math.Tan(FOV * 0.5f));
                float q = ZFar / (ZFar - ZNear);

                matrix.M11 = yScale / aspect;
                matrix.M22 = yScale;
                matrix.M33 = q;
                matrix.M34 = 1.0f;
                matrix.M43 = -q * ZNear;
                return matrix;
            }
        }

    }
}
