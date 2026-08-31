using System;
using HololensIKEA.Services;
using Xunit;

namespace HololensIKEA.Tests
{
    public class ModelService3DTests
    {
        [Fact]
        public void FindModelUrl_AbsoluteGlbAttribute_ReturnsModelUrl()
        {
            var page = new Uri("https://www.ikea.com/us/en/p/billy-12345678/");
            var html = "<model-viewer src=\"https://models.example/billy.glb?quality=high\"></model-viewer>";

            var modelUrl = ModelService3D.FindModelUrl(html, page);

            Assert.Equal("https://models.example/billy.glb?quality=high", modelUrl?.ToString());
        }

        [Fact]
        public void FindModelUrl_RelativeGltfModelAttribute_ResolvesAgainstPage()
        {
            var page = new Uri("https://www.ikea.com/us/en/p/billy-12345678/");

            var modelUrl = ModelService3D.FindModelUrl("<div gltf-model='/models/billy.glb'></div>", page);

            Assert.Equal("https://www.ikea.com/models/billy.glb", modelUrl?.ToString());
        }

        [Fact]
        public void FindModelUrl_EscapedAbsoluteGlbUrl_ReturnsModelUrl()
        {
            var page = new Uri("https://www.ikea.com/us/en/p/billy-12345678/");
            var html = "{\"model\":\"https:\\/\\/models.example\\/billy.glb\\u0026variant=mini\"}";

            var modelUrl = ModelService3D.FindModelUrl(html, page);

            Assert.Equal("https://models.example/billy.glb?variant=mini", modelUrl?.ToString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("<html><body>No 3D model</body></html>")]
        public void FindModelUrl_MissingModel_ReturnsNull(string html)
        {
            Assert.Null(ModelService3D.FindModelUrl(html, new Uri("https://www.ikea.com/")));
        }
    }
}