export { SbGate } from './sb-gate.js'
export { SbCase, SbAdapt } from './sb-adapt.js'
export { SbWidget } from './sb-widget.js'
export { sbCoordinator } from './coordinator.js'

if (typeof customElements !== 'undefined') {
  customElements.define('sb-gate', SbGate)
  customElements.define('sb-case', SbCase)
  customElements.define('sb-adapt', SbAdapt)
  customElements.define('sb-widget', SbWidget)
}
