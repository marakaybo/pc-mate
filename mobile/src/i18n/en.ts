import type { ru } from './ru';

export const en: typeof ru = {
  app: {
    name: 'PC MATE',
    tagline: 'Your PC, ready before you arrive'
  },

  tabs: {
    home: 'Home',
    scenarios: 'Scenarios',
    schedules: 'Schedules',
    settings: 'More'
  },

  common: {
    cancel: 'Cancel',
    save: 'Save',
    delete: 'Delete',
    close: 'Close',
    back: 'Back',
    retry: 'Retry',
    add: 'Add',
    edit: 'Edit',
    done: 'Done',
    yes: 'Yes',
    no: 'No',
    loading: 'Loading…',
    error: 'Error',
    ok: 'Got it',
    minutes: 'min',
    seconds: 's',
    never: 'never',
    enabled: 'on',
    disabled: 'off'
  },

  state: {
    running: 'Running',
    sleeping: 'Sleeping',
    hibernating: 'Hibernating',
    'shutting-down': 'Shutting down',
    locked: 'Locked',
    off: 'Off',
    unknown: 'No connection'
  },

  connection: {
    idle: 'Not connected',
    connecting: 'Connecting…',
    connected: 'Connected',
    offline: 'Offline',
    viaLan: 'home network',
    viaRelay: 'via server',
    lastSeen: 'Last seen: {{time}}'
  },

  home: {
    title: 'My computers',
    empty: 'No computers yet',
    emptyHint: 'Install PC MATE on your computer and scan the QR code it shows.',
    addPc: 'Add a computer',
    turnOn: 'Turn on',
    turnOff: 'Turn off',
    quickActions: 'Quick actions',
    favorites: 'Favourite scenarios',
    noFavorites: 'Star a scenario to launch it from here.',
    arrive: 'Arriving in…',
    wizard: 'Readiness check',
    history: 'History'
  },

  power: {
    sleep: 'Sleep',
    hibernate: 'Hibernate',
    reboot: 'Restart',
    lock: 'Lock',
    shutdown: 'Shut down',
    cancel: 'Cancel action',
    confirmShutdownTitle: 'Shut down the computer?',
    confirmShutdownBody: 'A countdown window appears on the PC — it can be cancelled from there.',
    countdown: 'Runs in {{seconds}}s',
    cancelled: 'Action cancelled',
    sent: 'Command sent',
    forceLabel: 'Close apps without saving'
  },

  wake: {
    title: 'Turning the computer on',
    viaLan: 'Sending a magic packet over the home network…',
    viaBridge: 'Asking the wake bridge to turn the computer on…',
    waiting: 'Waiting for the computer. This usually takes 30–60 seconds.',
    success: 'The computer is online',
    failed: 'The computer did not respond',
    failedHint:
      'Check power and Wake-on-LAN settings in the readiness wizard. Outside the home network you need a wake bridge.',
    noMac: 'The computer has no stored MAC address — Wake-on-LAN is not possible.',
    notOnWifi: 'The phone is not on the home network. Only a wake bridge can turn the computer on.',
    noBridge: 'No wake bridge configured.'
  },

  pair: {
    title: 'Add a computer',
    scanHint: 'Point the camera at the QR code in the PC MATE window on your computer.',
    permissionTitle: 'Camera access needed',
    permissionBody: 'The camera is only used to read the pairing QR code.',
    permissionButton: 'Allow',
    manualEntry: 'Enter the code manually',
    manualPlaceholder: 'pcmate://pair?d=…',
    connecting: 'Connecting to the computer…',
    expired: 'The code has expired. Press “New code” on the computer.',
    invalid: 'This is not a PC MATE code.',
    success: 'Computer connected',
    fingerprintTitle: 'Key fingerprint',
    fingerprintHint: 'It must match the one shown on the computer.',
    failedLan: 'Direct connection failed. Trying the server…',
    failedAll: 'Could not connect directly or through the server.'
  },

  scenarios: {
    title: 'Scenarios',
    empty: 'No scenarios yet',
    emptyHint: 'A scenario is a chain of actions: open Steam, wait, start OBS, set the volume.',
    create: 'New scenario',
    run: 'Run',
    running: 'Running…',
    done: 'Finished',
    failed: 'Finished with errors',
    stepsCount: 'steps: {{count}}',
    favorite: 'Favourite',
    nameLabel: 'Name',
    namePlaceholder: 'Evening: gaming and recording',
    onError: 'On step error',
    onErrorContinue: 'continue',
    onErrorStop: 'stop',
    addStep: 'Add a step',
    pickApp: 'Pick an app',
    appsLoading: 'Asking the computer for its app list…',
    appsEmpty: 'The computer sent no app list.',
    deleteConfirm: 'Delete the scenario “{{name}}”?',
    report: 'Report'
  },

  steps: {
    launch: 'Launch an app',
    open: 'Open a file or folder',
    url: 'Open a website',
    close: 'Close an app',
    wait: 'Wait',
    volume: 'Volume',
    command: 'Run a command',
    power: 'Power action',
    notify: 'Notify my phone',
    uwp: 'Store app',
    pathLabel: 'Path',
    argsLabel: 'Arguments',
    urlLabel: 'Address',
    processLabel: 'Process name',
    secondsLabel: 'Seconds',
    volumeLabel: 'Level, %',
    muteLabel: 'Mute',
    textLabel: 'Text',
    commandDisabled: 'Running commands is disabled on the computer — enable it in the agent settings.'
  },

  schedules: {
    title: 'Schedules',
    empty: 'No schedules yet',
    emptyHint: 'For example: “Mon–Fri at 23:00 turn on and run the Evening scenario”.',
    create: 'New schedule',
    action: 'Action',
    time: 'Time',
    days: 'Days',
    scenario: 'Scenario after wake-up',
    noScenario: 'no scenario',
    synced: 'Synced with the computer',
    notSynced: 'Waiting to sync',
    notSyncedHint: 'The computer will create the wake timer once it is online.',
    nextRun: 'Next run: {{time}}',
    everyday: 'every day',
    weekdays: 'weekdays',
    weekend: 'weekends',
    once: 'once',
    wakeBefore: 'Wake up earlier, min',
    deleteConfirm: 'Delete the schedule “{{name}}”?',
    actions: {
      wake: 'Turn on',
      shutdown: 'Shut down',
      sleep: 'Sleep',
      hibernate: 'Hibernate',
      reboot: 'Restart',
      scenario: 'Run a scenario'
    }
  },

  arrive: {
    title: 'Arriving in…',
    subtitle: 'The computer will turn on and get ready before you arrive.',
    minutes: 'In {{count}} {{unit}}',
    scenario: 'What to run',
    set: 'Set the timer',
    cancel: 'Cancel the timer',
    active: 'The computer wakes at {{time}}',
    done: 'Timer set'
  },

  wizard: {
    title: 'Computer readiness',
    subtitle: 'Checking whether the computer can wake on command and on schedule.',
    run: 'Check',
    fix: 'Fix',
    fixing: 'Fixing…',
    fixed: 'Fixed',
    summaryOk: 'The computer is ready: every check passed.',
    summaryWarn: 'The essentials are set, with notes: {{warn}}.',
    summaryFail: 'Need attention: {{fail}}.',
    testWol: 'Wake-on-LAN test',
    testTimer: 'Wake timer test',
    testWolHint:
      'The computer sleeps for 2 minutes. While it sleeps, press “Turn on” — the phone must be on the home network.',
    testTimerHint: 'The computer sleeps and should wake up by itself after 2 minutes.',
    testStarted: 'Test started',
    manualSteps: 'What to do by hand'
  },

  history: {
    title: 'History',
    empty: 'No events yet'
  },

  settings: {
    title: 'Settings',
    computers: 'Computers',
    language: 'Language',
    biometric: 'Biometric unlock',
    biometricHint: 'Ask for fingerprint or face when opening the app.',
    confirmShutdown: 'Confirm before shutting down',
    preferLan: 'Try the home network first',
    preferLanHint: 'Faster and works without the internet when the phone is at home.',
    notifications: 'Notifications',
    about: 'About',
    version: 'Version {{version}}',
    forget: 'Disconnect computer',
    forgetConfirm: 'Disconnect “{{name}}”? The phone will no longer control it.',
    agentSettings: 'Agent settings',
    countdown: 'Countdown, seconds',
    relayUrl: 'Relay server',
    diagnostics: 'Diagnostics'
  },

  errors: {
    noConnection: 'No connection to the computer.',
    notPaired: 'The computer is not connected.',
    commandFailed: 'Command failed: {{message}}',
    timeout: 'The computer did not respond in time.'
  }
};
