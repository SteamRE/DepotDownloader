// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using ProtoBuf;
using ProtoBuf.Meta;

namespace DepotDownloader
{
#pragma warning disable PBN9001
    [ProtoModel]
    [ProtoSerializable(typeof(AccountSettingsStore))]
    [ProtoSerializable(typeof(DepotConfigStore))]
    [ProtoSerializable(typeof(ProtoManifest))]
    sealed partial class ProtobufModel : TypeModel
    {
    }
#pragma warning restore PBN9001
}
