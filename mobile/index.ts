// Полифил crypto.getRandomValues обязан встать раньше всего остального:
// на нём держится генерация ключей и nonce в @noble.
import 'react-native-get-random-values';

import { registerRootComponent } from 'expo';

import App from './App';

// registerRootComponent вызывает AppRegistry.registerComponent('main', () => App)
// и настраивает окружение одинаково для Expo Go и обычной сборки.
registerRootComponent(App);
