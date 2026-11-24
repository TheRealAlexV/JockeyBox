/* Topology miniport: one bridge pair describing a silent capture endpoint. */
#pragma once

#include "jbx.h"

class CMiniportTopology : public IMiniportTopology, public CUnknownBase
{
public:
    JBX_STD_UNKNOWN();
    IMP_IMiniportTopology;

    CMiniportTopology();
    ~CMiniportTopology();

    static NTSTATUS CreateInstance(
        _Out_ CMiniportTopology ** OutMiniport);

private:
    void Destroy() override;

    PPORTTOPOLOGY m_Port;
};
