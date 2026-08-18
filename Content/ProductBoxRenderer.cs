using System;
using System.Numerics;
using HololensIKEA.Common;
using Windows.UI.Input.Spatial;

namespace HololensIKEA.Content
{
    /// <summary>
    /// Renders a static box scaled to product dimensions (width × height × depth in metres).
    /// Uses the same shader pipeline as SpinningCubeRenderer.
    /// </summary>
    internal class ProductBoxRenderer : Disposer
    {
        private DeviceResources                     deviceResources;

        private SharpDX.Direct3D11.InputLayout      inputLayout;
        private SharpDX.Direct3D11.Buffer           vertexBuffer;
        private SharpDX.Direct3D11.Buffer           indexBuffer;
        private SharpDX.Direct3D11.VertexShader     vertexShader;
        private SharpDX.Direct3D11.GeometryShader   geometryShader;
        private SharpDX.Direct3D11.PixelShader      pixelShader;
        private SharpDX.Direct3D11.Buffer           modelConstantBuffer;

        private ModelConstantBuffer                 modelConstantBufferData;
        private int                                 indexCount      = 0;
        private bool                                loadingComplete = false;
        private bool                                usingVprtShaders = false;

        private Vector3                             position     = new Vector3(0f, 0f, -2f);
        private float                               widthMeters  = 0.5f;
        private float                               heightMeters = 0.5f;
        private float                               depthMeters  = 0.5f;
        private Quaternion                          _rotation    = Quaternion.Identity;

        public Vector3 Position => position;

        public ProductBoxRenderer(DeviceResources deviceResources)
        {
            this.deviceResources = deviceResources;
            this.CreateDeviceDependentResourcesAsync();
        }

        /// <summary>Update product dimensions (metres). Applied immediately on next frame.</summary>
        public void SetDimensions(float width, float height, float depth)
        {
            widthMeters  = Math.Max(0.05f, width);
            heightMeters = Math.Max(0.05f, height);
            depthMeters  = Math.Max(0.05f, depth);
        }

        /// <summary>Repositions the box 2 m in front of the user's gaze.</summary>
        public void PositionHologram(SpatialPointerPose pointerPose)
        {
            if (pointerPose != null)
                position = pointerPose.Head.Position + 2.0f * pointerPose.Head.ForwardDirection;
        }

        /// <summary>Sets the world-space position of the box directly.</summary>
        public void SetPosition(Vector3 pos) => position = pos;

        /// <summary>Sets the world-space rotation of the box.</summary>
        public void SetRotation(Quaternion rot) => _rotation = rot;

        /// <summary>Updates the model-constant buffer each frame.</summary>
        public void Update(StepTimer timer)
        {
            // Scale unit cube, rotate, then translate to world position.
            Matrix4x4 modelTransform =
                Matrix4x4.CreateScale(widthMeters, heightMeters, depthMeters) *
                Matrix4x4.CreateFromQuaternion(_rotation) *
                Matrix4x4.CreateTranslation(position);

            modelConstantBufferData.model = Matrix4x4.Transpose(modelTransform);

            if (!loadingComplete)
                return;

            deviceResources.D3DDeviceContext.UpdateSubresource(ref modelConstantBufferData, modelConstantBuffer);
        }

        /// <summary>Draws the product box using instanced stereo rendering (2 instances for L+R eyes).</summary>
        public void Render()
        {
            if (!loadingComplete)
                return;

            var context = deviceResources.D3DDeviceContext;

            int stride = SharpDX.Utilities.SizeOf<VertexPositionColor>();
            context.InputAssembler.SetVertexBuffers(0, new SharpDX.Direct3D11.VertexBufferBinding(vertexBuffer, stride, 0));
            context.InputAssembler.SetIndexBuffer(indexBuffer, SharpDX.DXGI.Format.R16_UInt, 0);
            context.InputAssembler.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            context.InputAssembler.InputLayout       = inputLayout;

            context.VertexShader.SetShader(vertexShader, null, 0);
            context.VertexShader.SetConstantBuffers(0, modelConstantBuffer);

            if (!usingVprtShaders)
                context.GeometryShader.SetShader(geometryShader, null, 0);

            context.PixelShader.SetShader(pixelShader, null, 0);

            context.DrawIndexedInstanced(indexCount, 2, 0, 0, 0);
        }

        public async void CreateDeviceDependentResourcesAsync()
        {
            ReleaseDeviceDependentResources();

            usingVprtShaders = deviceResources.D3DDeviceSupportsVprt;
            var folder = Windows.ApplicationModel.Package.Current.InstalledLocation;

            var vsFileName = usingVprtShaders
                ? "Content\\Shaders\\VPRTVertexShader.cso"
                : "Content\\Shaders\\VertexShader.cso";

            var vsBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync(vsFileName));
            vertexShader = this.ToDispose(new SharpDX.Direct3D11.VertexShader(deviceResources.D3DDevice, vsBytes));

            SharpDX.Direct3D11.InputElement[] vertexDesc =
            {
                new SharpDX.Direct3D11.InputElement("POSITION", 0, SharpDX.DXGI.Format.R32G32B32_Float,  0, 0, SharpDX.Direct3D11.InputClassification.PerVertexData, 0),
                new SharpDX.Direct3D11.InputElement("COLOR",    0, SharpDX.DXGI.Format.R32G32B32_Float, 12, 0, SharpDX.Direct3D11.InputClassification.PerVertexData, 0),
            };
            inputLayout = this.ToDispose(new SharpDX.Direct3D11.InputLayout(deviceResources.D3DDevice, vsBytes, vertexDesc));

            if (!usingVprtShaders)
            {
                var gsBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync("Content\\Shaders\\GeometryShader.cso"));
                geometryShader = this.ToDispose(new SharpDX.Direct3D11.GeometryShader(deviceResources.D3DDevice, gsBytes));
            }

            var psBytes = await DirectXHelper.ReadDataAsync(await folder.GetFileAsync("Content\\Shaders\\PixelShader.cso"));
            pixelShader = this.ToDispose(new SharpDX.Direct3D11.PixelShader(deviceResources.D3DDevice, psBytes));

            // Unit cube: vertices from -0.5 to +0.5. Scale is applied via the model matrix.
            // Neutral light-gray color on all faces so non-sprite faces are unobtrusive.
            const float g = 0.78f;
            VertexPositionColor[] cubeVertices =
            {
                new VertexPositionColor(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(g, g, g)),
                new VertexPositionColor(new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(g, g, g)),
                new VertexPositionColor(new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(g, g, g)),
                new VertexPositionColor(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(g, g, g)),
                new VertexPositionColor(new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(g, g, g)),
                new VertexPositionColor(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(g, g, g)),
                new VertexPositionColor(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(g, g, g)),
                new VertexPositionColor(new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(g, g, g)),
            };

            vertexBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                deviceResources.D3DDevice, SharpDX.Direct3D11.BindFlags.VertexBuffer, cubeVertices));

            ushort[] cubeIndices =
            {
                2,1,0, // -x
                2,3,1,
                6,4,5, // +x
                6,5,7,
                0,1,5, // -y
                0,5,4,
                2,6,7, // +y
                2,7,3,
                0,4,6, // -z
                0,6,2,
                1,3,7, // +z
                1,7,5,
            };

            indexCount = cubeIndices.Length;
            indexBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                deviceResources.D3DDevice, SharpDX.Direct3D11.BindFlags.IndexBuffer, cubeIndices));

            modelConstantBuffer = this.ToDispose(SharpDX.Direct3D11.Buffer.Create(
                deviceResources.D3DDevice, SharpDX.Direct3D11.BindFlags.ConstantBuffer, ref modelConstantBufferData));

            loadingComplete = true;
        }

        public void ReleaseDeviceDependentResources()
        {
            loadingComplete = false;
            this.RemoveAndDispose(ref vertexShader);
            this.RemoveAndDispose(ref inputLayout);
            this.RemoveAndDispose(ref vertexBuffer);
            this.RemoveAndDispose(ref indexBuffer);
            this.RemoveAndDispose(ref geometryShader);
            this.RemoveAndDispose(ref pixelShader);
            this.RemoveAndDispose(ref modelConstantBuffer);
        }
    }
}
