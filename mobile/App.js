import { useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  StatusBar,
  StyleSheet,
  Text,
  TextInput,
  View
} from 'react-native';
import { WebView } from 'react-native-webview';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { useKeepAwake } from 'expo-keep-awake';
import { CameraView, useCameraPermissions } from 'expo-camera';

const STORAGE_KEY = 'lolremote.pcUrl';
const DEFAULT_PORT = 8765;

function normalizeAddress(value) {
  const text = value.trim();
  if (!text) return null;
  if (/^https?:\/\//i.test(text)) {
    try {
      const url = new URL(text);
      return `http://${url.host}/${url.search}`;
    } catch {
      return null;
    }
  }
  let host = text.replace(/\/+$/, '');
  if (!/:\d+$/.test(host)) host = `${host}:${DEFAULT_PORT}`;
  return `http://${host}/`;
}

export default function App() {
  useKeepAwake();
  const [loaded, setLoaded] = useState(false);
  const [address, setAddress] = useState(null);
  const [input, setInput] = useState('');
  const [failed, setFailed] = useState(false);
  const [reloadKey, setReloadKey] = useState(0);
  const webViewRef = useRef(null);
  const [scanning, setScanning] = useState(false);
  const [permission, requestPermission] = useCameraPermissions();
  const scannedRef = useRef(false);

  useEffect(() => {
    AsyncStorage.getItem(STORAGE_KEY)
      .then(saved => {
        if (saved) {
          setAddress(saved);
          setInput(saved);
        }
      })
      .finally(() => setLoaded(true));
  }, []);

  const connect = async (value) => {
    const normalized = normalizeAddress(typeof value === 'string' ? value : input);
    if (!normalized) return;
    await AsyncStorage.setItem(STORAGE_KEY, normalized);
    setFailed(false);
    setAddress(normalized);
    setInput(normalized);
    setReloadKey(k => k + 1);
  };

  const startScan = async () => {
    if (!permission?.granted) {
      const result = await requestPermission();
      if (!result.granted) return;
    }
    scannedRef.current = false;
    setScanning(true);
  };

  const onScanned = ({ data }) => {
    if (scannedRef.current || !/^https?:\/\//i.test(data || '')) return;
    scannedRef.current = true;
    setScanning(false);
    connect(data);
  };

  const editAddress = () => {
    setFailed(false);
    setAddress(null);
  };

  if (!loaded) {
    return (
      <View style={styles.center}>
        <StatusBar barStyle="light-content" />
        <ActivityIndicator color="#c8aa6e" size="large" />
      </View>
    );
  }

  if (scanning) {
    return (
      <View style={styles.page}>
        <StatusBar barStyle="light-content" />
        <CameraView
          style={styles.webview}
          facing="back"
          barcodeScannerSettings={{ barcodeTypes: ['qr'] }}
          onBarcodeScanned={onScanned}
        />
        <View style={styles.scanFrame} pointerEvents="none" />
        <Text style={styles.scanHint}>Tartsd a PC-s LolRemote ablak QR-kódja fölé</Text>
        <Pressable style={[styles.button, styles.scanCancel]} onPress={() => setScanning(false)}>
          <Text style={styles.buttonText}>Mégse</Text>
        </Pressable>
      </View>
    );
  }

  if (!address) {
    return (
      <KeyboardAvoidingView style={styles.setup} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
        <StatusBar barStyle="light-content" />
        <Text style={styles.brand}>
          LOL<Text style={styles.brandAccent}>REMOTE</Text>
        </Text>
        <Text style={styles.title}>Kapcsolódás a PC-hez</Text>
        <Text style={styles.text}>
          Indítsd el a LolRemote-ot a gépen, és olvasd be az ablakában látható QR-kódot.
          A telefonnak és a PC-nek ugyanazon a WiFi-n kell lennie.
        </Text>
        <Pressable style={({ pressed }) => [styles.button, styles.scanButton, pressed && styles.pressed]} onPress={startScan}>
          <Text style={styles.buttonText}>QR-kód beolvasása</Text>
        </Pressable>
        <Text style={styles.or}>vagy írd be a címet kézzel:</Text>
        <TextInput
          style={styles.input}
          value={input}
          onChangeText={setInput}
          placeholder="192.168.1.23"
          placeholderTextColor="#5b6680"
          autoCapitalize="none"
          autoCorrect={false}
          keyboardType="numbers-and-punctuation"
          returnKeyType="go"
          onSubmitEditing={connect}
        />
        <Pressable style={({ pressed }) => [styles.button, styles.secondary, pressed && styles.pressed]} onPress={connect}>
          <Text style={[styles.buttonText, styles.secondaryText]}>Csatlakozás</Text>
        </Pressable>
      </KeyboardAvoidingView>
    );
  }

  if (failed) {
    return (
      <View style={styles.center}>
        <StatusBar barStyle="light-content" />
        <Text style={styles.title}>Nem érem el a PC-t</Text>
        <Text style={[styles.text, styles.centerText]}>
          Cím: {address}{'\n'}Ellenőrizd, hogy a PC-s program fut, és a telefon ugyanazon a WiFi-n van.
        </Text>
        <Pressable
          style={({ pressed }) => [styles.button, pressed && styles.pressed]}
          onPress={() => {
            setFailed(false);
            setReloadKey(k => k + 1);
          }}
        >
          <Text style={styles.buttonText}>Újrapróbálom</Text>
        </Pressable>
        <Pressable style={styles.link} onPress={editAddress}>
          <Text style={styles.linkText}>Cím módosítása</Text>
        </Pressable>
      </View>
    );
  }

  return (
    <View style={styles.page}>
      <StatusBar barStyle="light-content" />
      <WebView
        key={reloadKey}
        ref={webViewRef}
        source={{ uri: address }}
        style={styles.webview}
        originWhitelist={['*']}
        javaScriptEnabled
        domStorageEnabled
        mediaPlaybackRequiresUserAction={false}
        allowsInlineMediaPlayback
        allowsBackForwardNavigationGestures={false}
        bounces={false}
        contentInsetAdjustmentBehavior="never"
        setSupportMultipleWindows={false}
        startInLoadingState
        renderLoading={() => (
          <View style={styles.loadingOverlay}>
            <ActivityIndicator color="#c8aa6e" size="large" />
          </View>
        )}
        onError={() => setFailed(true)}
        onHttpError={event => {
          if (event.nativeEvent.statusCode >= 500) setFailed(true);
        }}
      />
      <Pressable style={styles.settings} onPress={editAddress} hitSlop={12}>
        <Text style={styles.settingsText}>⚙︎</Text>
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  page: { flex: 1, backgroundColor: '#070b14' },
  webview: { flex: 1, backgroundColor: '#070b14' },
  loadingOverlay: {
    ...StyleSheet.absoluteFillObject,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#070b14'
  },
  center: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    padding: 28,
    backgroundColor: '#070b14'
  },
  setup: {
    flex: 1,
    justifyContent: 'center',
    padding: 28,
    backgroundColor: '#070b14'
  },
  brand: { color: '#f0e6d2', fontWeight: '800', letterSpacing: 3, fontSize: 16, marginBottom: 28 },
  brandAccent: { color: '#c8aa6e' },
  title: { color: '#e8ecf4', fontSize: 26, fontWeight: '700', marginBottom: 10 },
  text: { color: '#8b96ad', fontSize: 15, lineHeight: 21, marginBottom: 22 },
  centerText: { textAlign: 'center' },
  input: {
    height: 56,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: '#24304a',
    backgroundColor: '#0f1627',
    color: '#f0e6d2',
    fontSize: 20,
    paddingHorizontal: 16,
    marginBottom: 14
  },
  button: {
    height: 56,
    borderRadius: 12,
    backgroundColor: '#c8aa6e',
    alignItems: 'center',
    justifyContent: 'center',
    alignSelf: 'stretch'
  },
  pressed: { opacity: 0.8 },
  buttonText: { color: '#1a1408', fontSize: 18, fontWeight: '800', letterSpacing: 1 },
  link: { marginTop: 18, padding: 8 },
  linkText: { color: '#8b96ad', textDecorationLine: 'underline' },
  settings: {
    position: 'absolute',
    right: 14,
    bottom: 34,
    width: 40,
    height: 40,
    borderRadius: 20,
    backgroundColor: 'rgba(15, 22, 39, 0.85)',
    borderWidth: 1,
    borderColor: '#24304a',
    alignItems: 'center',
    justifyContent: 'center'
  },
  settingsText: { color: '#8b96ad', fontSize: 18 },
  scanButton: { marginBottom: 22 },
  or: { color: '#8b96ad', fontSize: 14, marginBottom: 10 },
  secondary: { backgroundColor: 'transparent', borderWidth: 1, borderColor: '#24304a' },
  secondaryText: { color: '#e8ecf4' },
  scanFrame: {
    position: 'absolute', alignSelf: 'center', top: '28%', width: 240, height: 240,
    borderWidth: 3, borderColor: '#c8aa6e', borderRadius: 24
  },
  scanHint: { position: 'absolute', top: '18%', alignSelf: 'center', color: '#f0e6d2', fontSize: 16, fontWeight: '600' },
  scanCancel: { position: 'absolute', bottom: 50, left: 28, right: 28, alignSelf: 'auto' }
});
