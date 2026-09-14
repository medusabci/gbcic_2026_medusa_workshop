# BUILT-IN MODULES
import time
import os.path
import multiprocessing as mp
import threading
# EXTERNAL MODULES
from PySide6.QtWidgets import QApplication
# MEDUSA-KERNEL MODULES
from medusa import components
from medusa import meeg, emg, nirs, ecg
from medusa.bci.ssvep_spellers import *
# MEDUSA MODULES
import resources, exceptions
import constants as mds_constants
from gui import gui_utils
# APP MODULES
from . import app_controller
from . import app_constants
from acquisition.lsl_utils import lsl_channel_info_to_eeg_channel_set


class App(resources.AppSkeleton):
    """ Main class of the application. For detailed comments about all
        functions, see the superclass code in resources module."""
    def __init__(self, app_info, app_settings, medusa_interface,
                 app_state, run_state, working_lsl_streams_info, rec_info):
        # Call superclass constructor
        super().__init__(app_info, app_settings, medusa_interface, app_state,
                         run_state, working_lsl_streams_info, rec_info)
        # Set attributes
        self.app_controller = None
        self.app_name = app_info["name"]
        # Colors
        theme_colors = gui_utils.get_theme_colors('dark')
        self.log_color = theme_colors['THEME_TEXT_ACCENT']

        # Find EEG
        self.main_lsl_worker = None
        # Booleans
        self.unity_selection_required = False

        # Processing queues (decoding requests and results)
        self.queue_decoding_requests = mp.Queue()
        self.queue_decoding_results = mp.Queue()
        self.currently_processing_epoch = None
        self.currently_available_commands = dict()
        self.already_notified_trials = list()
        self.decoded_onset_idxs = list()

        # Initialize models
        self.cmd_model = None
        self.csd_model = None

        # Initialize commands_info
        commands_info = list()
        for m in range(1):
            matrix = self.app_settings.matrix
            commands_info_matrix = dict()
            cmd_counter = 0
            for idx in range(len(matrix.item_list)):
                target = matrix.item_list[idx]
                commands_info_matrix[cmd_counter] = \
                    target.to_serializable_obj()
                cmd_counter += 1
            commands_info.append(commands_info_matrix)

        # Initialize SSVEP data
        self.ssvep_data = SSVEPSpellerData(
            mode='test',
            paradigm_conf=[],
            commands_info=[commands_info],
            onsets=np.zeros((0,)),
            unit_idx=np.zeros((0,)).astype(int),
            level_idx=np.zeros((0,)).astype(int),
            matrix_idx=np.zeros((0,)).astype(int),
            trial_idx=np.zeros((0,)).astype(int),
            cmd_model=self.cmd_model,
            csd_model=self.csd_model,
            spell_result=[],
            control_state_result=[],
            fps_resolution=self.app_settings.run_settings.fps_resolution,
            stim_time=self.app_settings.run_settings.stim_time,
            stim_freq_range=[6.67, 15.0],
            spell_target=None,
            control_state_target=None,
            spell_result_items=[])

        # Debugging?
        self.is_debugging = False

    def handle_exception(self, ex):
        if not isinstance(ex, exceptions.MedusaException):
            raise ValueError('Unhandled exception')
        if isinstance(ex, exceptions.MedusaException):
            # Take actions
            if ex.importance == 'critical':
                if self.app_controller.unity_state != \
                        app_constants.UNITY_DOWN:
                    self.app_controller.send_command(
                        {"event_type": "exception"})
                self.app_controller.close()
                ex.set_handled(True)

    # ---------------------------- LSL transponder ----------------------------
    def check_lsl_config(self, working_lsl_streams_info):
        # This code is just for demonstration purposes, remove for app
        # development.
        if len(working_lsl_streams_info) != 1:
            raise exceptions.IncorrectLSLConfig()

    def set_main_lsl_worker(self):
        # There can only be one EEG source, which will be the main worker
        # TODO: configurable EEG main source
        for lsl_worker in list(self.lsl_workers.values()):
            if lsl_worker.receiver.lsl_stream.medusa_type == 'EEG':
                self.main_lsl_worker = lsl_worker
                self.main_lsl_worker.channel_set = meeg.EEGChannelSet()
                self.main_lsl_worker.channel_set.set_standard_montage(
                    self.main_lsl_worker.receiver.l_cha,
                    allow_unlocated_channels=True)

    def check_settings_config(self, app_settings):
        # This code is just for demonstration purposes, remove for app
        # development. The IP address could have any value.
        if app_settings.connection_settings.ip != '127.0.0.1':
            raise exceptions.IncorrectSettingsConfig(
                f"Incorrect IP address: "
                f"{app_settings.connection_settings.ip}")

    def get_lsl_worker(self):
        """Returns the LSL worker"""
        return list(self.lsl_workers.values())[0]

    @exceptions.error_handler(scope='app')
    def load_models(self):
        # Load command decoding model
        self.cmd_model = CMDModelCCA()
        self.cmd_model.configure()
        self.cmd_model.build()

    # ---------------------------- LOG ----------------------------
    def send_to_log(self, msg):
        """ Styles a message to be sent to the main MEDUSA log."""
        self.medusa_interface.log(
            msg, {'color': self.log_color, 'font-style': 'italic'})

    # ---------------------------- MANAGER THREAD ----------------------------
    def manager_thread_worker(self):
        TAG = '[apps/dev_app_unity/App/manager_thread_worker]'
        # Function to close everything
        def close_everything():
            # Notify Unity that it must stop
            self.app_controller.stop()
            print(TAG, 'Close signal emitted to Unity.')
            # Wait until the Unity server notify us that the app is closed
            while self.app_controller.unity_state.value != \
                    app_constants.UNITY_FINISHED:
                pass
            print(TAG, 'Unity application closed!')
            # Exit the loop
            self.stop = True
        # Wait until MEDUSA is ready
        print(TAG, "Waiting MEDUSA to be ready...")
        while self.run_state.value != mds_constants.RUN_STATE_READY:
            time.sleep(0.1)
        # Wait until the app_controller is initialized
        while self.app_controller is None:
            time.sleep(0.1)
        # Set up the TCP server and wait for the Unity client
        self.app_controller.start_server()
        self.send_to_log(f'[{self.app_name}] TCP server listening!')
        # Wait until UNITY is UP and send the parameters
        while self.app_controller.unity_state.value == \
                app_constants.UNITY_DOWN:
            time.sleep(0.1)
        self.app_controller.send_parameters()
        # Wait until UNITY is ready
        while self.app_controller.unity_state.value == \
                app_constants.UNITY_UP:
            time.sleep(0.1)
        self.send_to_log(f'[{self.app_name}] Unity is ready to start')
        # Change app state to power on
        self.medusa_interface.app_state_changed(
            mds_constants.APP_STATE_ON)
        # If play is pressed
        while self.run_state.value == mds_constants.RUN_STATE_READY:
            time.sleep(0.1)
        if self.run_state.value == mds_constants.RUN_STATE_RUNNING:
            self.app_controller.play()
        # Check for an early stop
        if self.run_state.value == mds_constants.RUN_STATE_STOP:
            close_everything()
        # Loop
        while not self.stop:
            # Check for pause
            if self.run_state.value == mds_constants.RUN_STATE_PAUSED:
                self.app_controller.pause()
                while self.run_state.value == mds_constants.RUN_STATE_PAUSED:
                    time.sleep(0.1)
                # If resumed
                if self.run_state.value == mds_constants.RUN_STATE_RUNNING:
                    self.app_controller.resume()

            # Check for stop
            if self.run_state.value == mds_constants.RUN_STATE_STOP:
                close_everything()

            # If we are not processing anything, we can process the next request
            if self.currently_processing_epoch is None and \
                    not self.queue_decoding_requests.empty():
                self.currently_processing_epoch = \
                    self.queue_decoding_requests.get()

            # Processing is required
            if self.currently_processing_epoch is not None:
                if self.cmd_model is None:
                    raise Exception('[BCI maze] Cannot process the trial '
                                    'if the model has not been trained before!')
                # We need to wait until the signal from the last onset is
                # enough to extract the full epoch
                threading.Thread(
                    target=self.process_trial,
                    args=(self.currently_processing_epoch,),
                    name="ssvep_speller_decoding"
                ).start()

                # The processing task has been threaded, so we can
                # proceed with the next request
                self.currently_processing_epoch = None

            # Check for a successful decoding
            if not self.queue_decoding_results.empty() and \
                    self.app_controller is not None:
                dec_epoch, decoding = self.queue_decoding_results.get()
                dec_trial, dec_cycle, dec_onset = dec_epoch
                if dec_trial in self.already_notified_trials:
                    print(TAG, "Ignoring decoding for (trial: %i, cycle: %i, "
                               "onset_idx: %i), it was already notified!" %
                          (dec_epoch[0], dec_epoch[1], dec_epoch[2]))
                else:
                    # If the processing was required by Unity itself
                    if self.unity_selection_required:
                        self.unity_selection_required = False
                        # Notify UNITY about the selected character
                        self.app_controller.notify_selection(
                            selection_uid=decoding['uid'],
                            selection_cycle=dec_cycle)
                        # Append the selected result
                        self.already_notified_trials.append(dec_trial)
                        self.decoded_onset_idxs.append(dec_onset)
                        self.ssvep_data.spell_result.append(decoding['uid'])
        print(TAG, 'Terminated')

    def process_event(self, dict_event):
        """ Process any interesting event.

            These events may be called by the `manager_thread_worker` whenever
            Unity requests any kind-of processing. As we do not have any MEDUSA
            GUI, this function call be also directly called by the instance of
            ``AppController`` if necessary.
        """
        # self.send_to_log('Message from Unity: %s' % str(event))
        if dict_event["event_type"] ==  "test":
            # Onset information.
            self.append_trial_info(dict_event)
        elif dict_event["event_type"] == "processPlease":
            # Unity is requesting MEDUSA to process the previous trial
            self.unity_selection_required = True
            self.queue_decoding_requests.put((
                self.ssvep_data.trial_idx[-1], 0,
                len(self.ssvep_data.onsets) - 1))
        elif dict_event["event_type"] == "finish":
            time.sleep(5)
            self.medusa_interface.run_state_changed(
                mds_constants.RUN_STATE_FINISHED)
        else:
            print(self.TAG, 'Unknown event_type %s' % dict_event["event_type"])

    # ---------------------------- MAIN PROCESS ----------------------------
    def main(self):
        """ Controls the main life cycle of the ``App`` class.

            First, changes the app state to powering on and sets up the
            ``AppController`` instance. Then, changes the app state to on. It
            waits until the TCP Server instantiated by the ``AppController`` is
            up, and afterward tells the ``AppController`` to open the Unity's
            .exe application, which is a blocking process. When the application
            is closed, this function changes the app state to powering off and
            shows a dialog to save the file (only if we have data available).
            Finally, it changes the app state to off and dies.
        """
        # 1 - Change app state to powering on
        self.medusa_interface.app_state_changed(
            mds_constants.APP_STATE_POWERING_ON)
        # 2 - Set up the controller that starts the TCP server
        self.set_main_lsl_worker()
        self.load_models()
        self.app_controller = app_controller.AppController(
            callback=self,
            app_settings=self.app_settings,
            run_state=self.run_state)
        # 3 - Wait until server is UP, start the unity app and block the
        # execution until it is closed
        while self.app_controller.server_state.value == app_constants.SERVER_DOWN:
            time.sleep(0.1)
        # 4 - Start application (blocking method)
        if self.is_debugging:
            # When debugging
            while self.app_controller:
                time.sleep(1)
        else:
            # Start application (blocking method)
            self.app_controller.start_application()
            time.sleep(1)
        # 5 - Close
        if self.app_controller.server_state.value != app_constants.SERVER_DOWN:
            self.app_controller.close()
        while self.app_controller.server_state.value == app_constants.SERVER_UP:
            time.sleep(0.1)
        # 6 - Change app state to powering off
        self.medusa_interface.app_state_changed(
            mds_constants.APP_STATE_POWERING_OFF)
        # 7 - Stop working threads
        self.stop_working_threads()
        # 8 - Save recording
        qt_app = QApplication()
        file_path = self.get_file_path_from_rec_info()
        rec_streams_info = self.get_rec_streams_info()
        if file_path is None:
            # Display save dialog to retrieve file_info
            self.save_file_dialog = resources.SaveFileDialog(
                rec_info=self.rec_info,
                rec_streams_info=rec_streams_info,
                app_ext=self.app_info['extension'],
                allowed_formats=self.allowed_formats)
            self.save_file_dialog.accepted.connect(self.on_save_rec_accepted)
            self.save_file_dialog.rejected.connect(self.on_save_rec_rejected)
            qt_app.exec()
        else:
            # Save file automatically
            self.save_recording(file_path, rec_streams_info)
        # 9 - Change app state to power off
        self.medusa_interface.app_state_changed(
            mds_constants.APP_STATE_OFF)

    # ---------------------------- SAVE DATA ----------------------------
    @exceptions.error_handler(scope='app')
    def on_save_rec_accepted(self):
        file_path, self.rec_info = self.save_file_dialog.get_rec_info()
        rec_streams_info = self.save_file_dialog.get_rec_streams_info()
        self.save_recording(file_path, rec_streams_info)

    @exceptions.error_handler(scope='app')
    def on_save_rec_rejected(self):
        pass

    @exceptions.error_handler(scope='app')
    def save_recording(self, file_path, rec_streams_info):
        # Recording
        rec = components.Recording(
            subject_id=self.rec_info.pop('subject_id'),
            recording_id=self.rec_info.pop('rec_id'),
            date=time.strftime("%d-%m-%Y %H:%M", time.localtime()),
            **self.rec_info)
        # Experiment data
        exp_data = components.CustomExperimentData(
            **self.app_settings.to_serializable_obj())
        rec.add_experiment_data(exp_data, 'exp_data')
        # Streams data
        for lsl_stream in self.lsl_streams_info:
            if not rec_streams_info[lsl_stream.medusa_uid]['enabled']:
                continue
            # Get stream data class
            lsl_worker = self.lsl_workers[lsl_stream.medusa_uid]
            stream_data = lsl_worker.get_data_class()
            # Save stream
            att_key = rec_streams_info[lsl_stream.medusa_uid]['att-name']
            rec.add_biosignal(stream_data, att_key)
        # Save recording
        rec.save(file_path)
        # Print a message
        self.medusa_interface.log('Recording saved successfully')


    # ---------------------------- PROCESSING ----------------------------
    @exceptions.error_handler(scope='app')
    def get_eeg_data(self):
        # EEG data
        lsl_worker = self.get_lsl_worker()

        channels = lsl_channel_info_to_eeg_channel_set(
            lsl_worker.receiver.info_cha)
        times_, signal_ = lsl_worker.get_data()
        if times_.shape[0] != signal_.shape[0]:
            min_len = min(times_.shape[0], signal_.shape[0])
            print('[get_eeg_data] Warning! timestamps (%i) and '
                  'signal (%i) did not have the same dimensions, trimmed '
                  'both to have %i samples.' % (times_.shape[0],
                                                signal_.shape[0],
                                                min_len)
                  )
            times_ = times_[:min_len]
            signal_ = signal_[:min_len, :]
        return times_, signal_, lsl_worker.receiver.fs, channels, \
            lsl_worker.receiver.name

    def append_trial_info(self, msg):
        # Common trial info
        self.ssvep_data.onsets = np.append(
            self.ssvep_data.onsets, msg["onset"])
        self.ssvep_data.trial_idx = np.append(
            self.ssvep_data.trial_idx, int(msg["trial"]))
        self.ssvep_data.matrix_idx = np.append(
            self.ssvep_data.matrix_idx, int(msg["matrix_idx"]))
        self.ssvep_data.level_idx = np.append(
            self.ssvep_data.level_idx, int(msg["level_idx"]))
        self.ssvep_data.unit_idx = np.append(
            self.ssvep_data.unit_idx, int(msg["unit_idx"]))

    def process_trial(self, last_epoch=None):
        """ This function processes only the last trial to get the selected
        command. Note that this method is not called in TRAIN_MODE.

        Parameters
        -------------
        last_epoch: tuple(trial_idx, cycle_idx, onset_idx) or None
            Last epoch to be considered in the prediction.

        Returns
        ------------
        decoding: dict()
            Prediction.
        """
        if self.cmd_model is None:
            self.handle_exception(Exception('[BCI maze] Cannot process the '
                                            'trial if the model has not been'
                                            ' trained before!'))

        # Get current data
        fs = self.main_lsl_worker.receiver.fs
        w_epoch_t = [0, self.ssvep_data.stim_time * 1000]
        ready = False
        while not ready:
            times, signal = self.main_lsl_worker.get_data()
            epochs_feasibility = mds.check_epochs_feasibility(
                times, self.ssvep_data.onsets, fs, w_epoch_t)
            ready = epochs_feasibility == 'ok'

        # Process the last trial
        last_trial_idx = self.ssvep_data.trial_idx[-1]
        cmd, __ = self.cmd_model.predict(
            times=times, signal=signal,
            fs=self.main_lsl_worker.receiver.fs,
            channel_set=self.main_lsl_worker.channel_set,
            exp_data=self.ssvep_data,
            trial_idx=last_trial_idx)

        # Decoding info
        cmd_uid = cmd[0][0][0][1]
        decoding = dict()
        decoding['uid'] = cmd_uid

        # Put the decoding into the queue
        self.queue_decoding_results.put((last_epoch, decoding))

        return decoding