#define ONEVPL_EXPERIMENTAL
#include <stdio.h>
#include <stddef.h>
#include "vpl/mfx.h"
#define S(t) printf("SIZE %s %zu\n", #t, sizeof(t))
#define O(t,f) printf("OFF %s.%s %zu\n", #t, #f, offsetof(t,f))
#define C(c) printf("CONST %s %lld\n", #c, (long long)(c))
int main(void) {
  S(mfxVariant); O(mfxVariant,Version); O(mfxVariant,Type); O(mfxVariant,Data);
  S(mfxFrameInfo); O(mfxFrameInfo,FourCC); O(mfxFrameInfo,Width); O(mfxFrameInfo,Height); O(mfxFrameInfo,CropX); O(mfxFrameInfo,CropY);
  O(mfxFrameInfo,CropW); O(mfxFrameInfo,CropH); O(mfxFrameInfo,FrameRateExtN); O(mfxFrameInfo,FrameRateExtD);
  O(mfxFrameInfo,AspectRatioW); O(mfxFrameInfo,AspectRatioH); O(mfxFrameInfo,PicStruct); O(mfxFrameInfo,ChromaFormat);
  O(mfxFrameInfo,BitDepthLuma); O(mfxFrameInfo,BitDepthChroma);
  S(mfxInfoMFX); O(mfxInfoMFX,LowPower); O(mfxInfoMFX,BRCParamMultiplier); O(mfxInfoMFX,FrameInfo); O(mfxInfoMFX,CodecId);
  O(mfxInfoMFX,CodecProfile); O(mfxInfoMFX,CodecLevel); O(mfxInfoMFX,NumThread); O(mfxInfoMFX,TargetUsage); O(mfxInfoMFX,GopPicSize);
  O(mfxInfoMFX,GopRefDist); O(mfxInfoMFX,GopOptFlag); O(mfxInfoMFX,IdrInterval); O(mfxInfoMFX,RateControlMethod);
  O(mfxInfoMFX,InitialDelayInKB); O(mfxInfoMFX,BufferSizeInKB); O(mfxInfoMFX,TargetKbps); O(mfxInfoMFX,MaxKbps);
  O(mfxInfoMFX,NumSlice); O(mfxInfoMFX,NumRefFrame); O(mfxInfoMFX,EncodedOrder);
  S(mfxVideoParam); O(mfxVideoParam,AllocId); O(mfxVideoParam,AsyncDepth); O(mfxVideoParam,mfx); O(mfxVideoParam,Protected);
  O(mfxVideoParam,IOPattern); O(mfxVideoParam,ExtParam); O(mfxVideoParam,NumExtParam);
  S(mfxBitstream); O(mfxBitstream,DecodeTimeStamp); O(mfxBitstream,TimeStamp); O(mfxBitstream,Data); O(mfxBitstream,DataOffset);
  O(mfxBitstream,DataLength); O(mfxBitstream,MaxLength); O(mfxBitstream,PicStruct); O(mfxBitstream,FrameType); O(mfxBitstream,DataFlag);
  S(mfxFrameSurfaceInterface); O(mfxFrameSurfaceInterface,Context); O(mfxFrameSurfaceInterface,AddRef); O(mfxFrameSurfaceInterface,Release);
  O(mfxFrameSurfaceInterface,GetNativeHandle); O(mfxFrameSurfaceInterface,Synchronize);
  S(mfxFrameSurface1); O(mfxFrameSurface1,FrameInterface); O(mfxFrameSurface1,Info); O(mfxFrameSurface1,Data);
  S(mfxFrameData); O(mfxFrameData,TimeStamp); O(mfxFrameData,MemId); O(mfxFrameData,Locked);
  S(mfxExtBuffer); S(mfxExtVideoSignalInfo); O(mfxExtVideoSignalInfo,VideoFormat); O(mfxExtVideoSignalInfo,VideoFullRange);
  O(mfxExtVideoSignalInfo,ColourDescriptionPresent); O(mfxExtVideoSignalInfo,ColourPrimaries); O(mfxExtVideoSignalInfo,TransferCharacteristics);
  O(mfxExtVideoSignalInfo,MatrixCoefficients);
  S(mfxSurfaceHeader); O(mfxSurfaceHeader,SurfaceType); O(mfxSurfaceHeader,SurfaceFlags); O(mfxSurfaceHeader,StructSize);
  S(mfxSurfaceInterface); S(mfxSurfaceD3D11Tex2D); O(mfxSurfaceD3D11Tex2D,texture2D);
  S(mfxMemoryInterface); O(mfxMemoryInterface,ImportFrameSurface);
  C(MFX_VARIANT_VERSION); C(MFX_VARIANT_TYPE_U32); C(MFX_IMPL_TYPE_HARDWARE); C(MFX_ACCEL_MODE_VIA_D3D11);
  C(MFX_HANDLE_D3D11_DEVICE); C(MFX_HANDLE_MEMORY_INTERFACE); C(MFX_RESOURCE_DX11_TEXTURE);
  C(MFX_CODEC_AVC); C(MFX_FOURCC_NV12); C(MFX_CHROMAFORMAT_YUV420); C(MFX_PICSTRUCT_PROGRESSIVE);
  C(MFX_TARGETUSAGE_BEST_SPEED); C(MFX_RATECONTROL_CBR); C(MFX_CODINGOPTION_ON); C(MFX_CODINGOPTION_OFF); C(MFX_IOPATTERN_IN_VIDEO_MEMORY);
  C(MFX_PROFILE_AVC_MAIN); C(MFX_FRAMETYPE_IDR); C(MFX_FRAMETYPE_I);
  C(MFX_EXTBUFF_VIDEO_SIGNAL_INFO); C(MFX_SURFACE_TYPE_D3D11_TEX2D); C(MFX_SURFACE_FLAG_IMPORT_SHARED); C(MFX_SURFACE_FLAG_IMPORT_COPY);
  C(MFX_SURFACE_COMPONENT_ENCODE); C(MFX_SURFACEINTERFACE_VERSION); O(mfxSurfaceInterface,Version); O(mfxSurfaceInterface,Context); C(MFX_FRAMESURFACE1_VERSION);
  C(MFX_ERR_NONE); C(MFX_ERR_MORE_DATA); C(MFX_WRN_DEVICE_BUSY); C(MFX_WRN_IN_EXECUTION); C(MFX_WRN_INCOMPATIBLE_VIDEO_PARAM);
  C(MFX_ERR_NOT_ENOUGH_BUFFER); C(MFX_ERR_UNSUPPORTED); C(MFX_ERR_NOT_FOUND); C(MFX_ERR_INVALID_VIDEO_PARAM); C(MFX_ERR_UNDEFINED_BEHAVIOR);
  C(MFX_ERR_DEVICE_FAILED); C(MFX_ERR_NULL_PTR); C(MFX_ERR_MEMORY_ALLOC); C(MFX_ERR_NOT_INITIALIZED); C(MFX_ERR_INVALID_HANDLE);
  C(MFX_WRN_PARTIAL_ACCELERATION); C(MFX_ERR_GPU_HANG); C(MFX_ERR_ABORTED); C(MFX_ERR_LOCK_MEMORY); C(MFX_ERR_NOT_ENOUGH_BUFFER);
  C(MFX_VERSION_MAJOR); C(MFX_VERSION_MINOR);
  return 0;
}
