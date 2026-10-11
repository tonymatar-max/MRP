import { useState } from 'react';
import type { DataSource } from '../api/types';
import { useStatus } from '../api/hooks';
import { ItemsGrid } from '../components/masterdata/ItemsGrid';
import { SuppliersGrid } from '../components/masterdata/SuppliersGrid';
import { PageHeader } from '../components/ui';

export default function MasterData() {
  const status = useStatus().data;
  const [tab, setTab] = useState<'items' | 'suppliers'>('items');
  const [picked, setPicked] = useState<DataSource | null>(null);
  const source = picked ?? status?.defaultSource ?? 'mock';
  return (
    <>
      <PageHeader
        crumb="Planning"
        title="Master data"
        actions={
          <div className="field" style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}>
            <label htmlFor="md-source">Source</label>
            <select id="md-source" value={source} onChange={(e) => setPicked(e.target.value as DataSource)}>
              <option value="mock">Mock sample data</option><option value="b1-sql">SAP B1 (SQL)</option>
            </select>
          </div>
        }
      />
      <div className="page md-page">
        <div className="alert info-note" role="note">Changes are stored in Cobalt MRP and used by planning. They do not change SAP Business One.</div>
        <div className="tabs" role="tablist" aria-label="Master data">
          <button type="button" role="tab" id="tab-items" aria-selected={tab === 'items'} aria-controls="panel-items" className={`tab ${tab === 'items' ? 'active' : ''}`} onClick={() => setTab('items')}>Items</button>
          <button type="button" role="tab" id="tab-suppliers" aria-selected={tab === 'suppliers'} aria-controls="panel-suppliers" className={`tab ${tab === 'suppliers' ? 'active' : ''}`} onClick={() => setTab('suppliers')}>Suppliers</button>
        </div>
        <div role="tabpanel" id={`panel-${tab}`} aria-labelledby={`tab-${tab}`}>
          {tab === 'items' ? <ItemsGrid source={source} /> : <SuppliersGrid source={source} />}
        </div>
      </div>
    </>
  );
}
