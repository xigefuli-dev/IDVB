using OpenCvSharp;
using TorchSharp;
using static TorchSharp.torch;

namespace IDVBuff.Features.Maps;

internal static partial class MapLearningPreprocessor
{
    public static Tensor CreateGpuTrainingTensor(Mat source, Device device)
    {
        if (device.type != DeviceType.CUDA)
            throw new ArgumentException("GPU 训练输入需要 CUDA 设备。",
                nameof(device));
        return SiameseMapNetwork.ToTensor(
            CreateTrainingInputs(source), device);
    }
}
