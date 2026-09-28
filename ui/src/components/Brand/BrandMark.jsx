import React from 'react'
import { t } from '../../i18n'

// Company brand block — same logo treatment as DSA Vision Platform's side nav.
const BrandMark = ({ collapsed = false }) => (
  <div className={'dsaBrand' + (collapsed ? ' collapsed' : '')} aria-label="DSA Vehicle">
    <img src="/DSA_logo.png" alt="" aria-hidden="true" />
    {!collapsed && <div><strong>DSA Vehicle</strong><small>{t('Parking Access')}</small></div>}
  </div>
)

export default BrandMark
