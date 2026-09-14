from medusa.components import SerializableComponent
from medusa.bci.ssvep_spellers import *

class Settings(SerializableComponent):
    def __init__(self, connection_settings = None, run_settings = None):
        self.connection_settings = connection_settings if \
            connection_settings is not None else ConnectionSettings()
        self.run_settings = run_settings if \
            run_settings is not None else RunSettings()
        self.matrix = self._build_matrix()

    def _build_matrix(self):
        fps = self.run_settings.fps_resolution
        stim_time = self.run_settings.stim_time

        # Build the matrix
        matrix = SSVEPMatrix()
        generator = SSVEPCodeGenerator(stim_time=stim_time, fps=fps, base=2)
        freqs = [6.67, 8.57, 12, 15]
        for idx, item_name in enumerate(['Up', 'Down', 'Right', 'Left']):
            stim_freq = freqs[idx]
            target = SSVEPTarget(
                uid=item_name,
                stim_freq=stim_freq,
                sequence=generator.generate_seq(stim_freq).tolist())
            matrix.append(target)
        return matrix


    def to_serializable_obj(self):
        sett_dict = {'connection_settings': self.connection_settings.__dict__,
                     'run_settings': self.run_settings.__dict__}
        return sett_dict

    @staticmethod
    def from_serializable_obj(dict_data):
        connection_settings = ConnectionSettings(**dict_data[
                                                 "connection_settings"])
        run_settings = RunSettings(**dict_data["run_settings"])
        return Settings(connection_settings=connection_settings,
                   run_settings=run_settings)


class ConnectionSettings:
    def __init__(self, ip="127.0.0.1", port=50000):
        self.ip = ip
        self.port = port


class RunSettings:
    def __init__(self, stim_time=10,
                     fps_resolution=60):
        self.stim_time = stim_time
        self.fps_resolution = fps_resolution


class SSVEPMatrix:
    def __init__(self):
        """ Class that represents a SSVEP matrix.

        Attribute `item_list` encompasses a vector of commands.

        """

        self.item_list = []  # Vector of targets

    def remove(self, index):
        """ Removes a CVEPTarget element from the list of targets."""
        self.item_list.pop(index)

    def append(self, new_item):
        """ Appends a new CVEPTarget item to the list of targets. """
        if type(new_item) != SSVEPTarget:
            raise ValueError('Cannot append, object type is not CVEPTarget.')
        self.item_list.append(new_item)

    def serialize(self):
        items = []
        for i in self.item_list:
            items.append(i.to_serializable_obj())
        return {"item_list": items}


class SSVEPTarget:

    def __init__(self, uid='', stim_freq=None, sequence=None):
        """ Class that represents a target cell of the SSVEP matrix.

        Parameters
        ----------
        uid: basestring
            Unique identifier that will represent the target cell.
        stim_freq: float
            Stimulation frequency of the target cell.
        sequence : list, None
            Sequence that modulates this target item. The modulation will
            always start with the first index, i.e., sequence[0]
        """

        self.uid = uid
        self.stim_freq = stim_freq
        self.sequence = sequence

    def set_uid(self, uid):
        self.uid = uid

    def set_stim_freq(self, stim_freq):
        self.stim_freq = stim_freq

    def set_sequence(self, sequence):
        self.sequence = sequence

    def to_serializable_obj(self):
        return self.__dict__
