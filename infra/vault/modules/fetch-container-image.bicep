// Reads the image a Container App already runs, so a later provision keeps what `azd deploy` put there. This has to be its own
// module: a template that deploys an app and also references that same app (an `existing` resource of the same name) is
// refused by ARM as a circular dependency, which the first real second provision hit. As a separate deployment, the reference
// is to a resource outside the template that deploys it. The same shape as azd's own fetch-container-image module.
@description('true when the app exists (azd sets SERVICE_<name>_RESOURCE_EXISTS). false: nothing is read and image is empty.')
param exists bool
param name string

resource app 'Microsoft.App/containerApps@2025-07-01' existing = if (exists) { name: name }

output image string = exists ? app!.properties.template.containers[0].image : ''
