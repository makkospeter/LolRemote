const fs = require('fs');
const path = require('path');

const appDir = process.argv[2];
const file = path.join(appDir, 'app.json');
const config = JSON.parse(fs.readFileSync(file, 'utf8'));
const expo = config.expo || (config.expo = {});

expo.name = 'LolRemote';
expo.slug = 'lolremote';
expo.userInterfaceStyle = 'dark';
expo.backgroundColor = '#070b14';
expo.icon = './assets/lolremote-icon.png';
expo.splash = { backgroundColor: '#070b14', image: './assets/lolremote-icon.png', resizeMode: 'contain' };
expo.ios = {
  ...(expo.ios || {}),
  supportsTablet: false,
  bundleIdentifier: (expo.ios && expo.ios.bundleIdentifier) || 'com.lolremote.app',
  infoPlist: {
    ...((expo.ios && expo.ios.infoPlist) || {}),
    NSAppTransportSecurity: { NSAllowsArbitraryLoads: true, NSAllowsLocalNetworking: true },
    NSLocalNetworkUsageDescription: 'A LolRemote a helyi hálózaton keresztül kapcsolódik a PC-s programhoz.'
  }
};

expo.plugins = (expo.plugins || []).filter(p => p !== 'expo-camera' && !(Array.isArray(p) && p[0] === 'expo-camera'));
expo.plugins.push(['expo-camera', { cameraPermission: 'A LolRemote a kamerát a PC-s ablak QR-kódjának beolvasásához használja.' }]);

fs.writeFileSync(file, JSON.stringify(config, null, 2));
console.log('app.json frissítve');
